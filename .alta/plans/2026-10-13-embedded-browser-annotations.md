---
title: Embedded browser with element annotations and agent control
status: in-progress
created: 2026-10-13
summary: Give each session of CodeAlta Desktop a browser of its own with several pages (native views in the session pane), let the user annotate single elements of a page with change requests that agents read, and give sessions tools to drive that browser.
---

# Embedded browser with element annotations and agent control

## Goal

A tab of CodeAlta Desktop shows a real web page, typically the user's own application on `localhost`. The user points at an element and writes what should change. A session reads these annotations, with enough about each element to find it in the source, and can also drive the page itself: navigate, click, fill, read the console and the network, take snapshots and screenshots.

Desktop only. No TUI screen, dialog, command or setting (`AGENTS.md`). The shared `alta` tool gets a `browser` command group that only a host with a browser registers, as `alta terminal` does (`IAltaTerminals`, `src/CodeAlta.LiveTool/AltaTerminals.cs`).

Nothing was changed during research. This plan is a draft: the decisions of the next section are the user's.

## Decisions

| # | Decision | Answer | Why |
| --- | --- | --- | --- |
| 1 | Where the browser lives | **Decided by the user (2026-10-13): the browser belongs to a session.** It is a pane of the session, not a tab of the main area. Another session in front: its browser is not there. Back to the session: the browser is there again, as it was. A session can have several pages open, so the pane has a tab strip of its own. | As a tab of the main area nobody could tell which session a browser belongs to. |
| 2 | Whom annotations belong to | **The session** (follows from 1; recommended, to confirm). Kept in the application state, not in the repository. | The annotations are made in the browser of a session and are that session's work list. A screenshot per annotation does not belong in git. |
| 3 | Which addresses load | **Any `http`/`https` address** (recommended, to confirm), with the profile and the process of the browser isolated from the window's | A localhost-only rule breaks sign-in redirects, CDNs and documentation pages at once. The isolation below is what protects the application, not the address. |
| 4 | NeoAstra | **Windows first, on NeoAstra 1.3.1 as released** (recommended, to confirm), with the missing view API proposed upstream | NeoAstra is a package (`src/Directory.Packages.props:12`) of another repository (`github.com/NeoAstra/NeoAstra`); the feature should not wait for a release of it. macOS and Linux follow when the API exists. |

## What research established

**Automation is already there.** The UI tools wrap `NeoAutomation` (`src/CodeAlta/Desktop/Ui/DesktopUiAutomation.cs:39`), which has the tool set of Chrome DevTools MCP and makes every view of the application a page.

- `NeoAutomationOptions.PageFilter` selects which views are pages and `NewPageHandler` creates the view of `new_page` (`../NeoAstra/src/NeoAstra.Core/Automation/NeoAutomationTypes.cs:14,21`).
- Today the window's automation has no filter: a second view would silently become a page of `alta ui` and of the `codealta-dev` MCP server. That must be closed in the same change that creates the first browser view.
- The automation works inside the page with a script in the page's own world (`window.__neoastraAutomation`, `NeoAutomationAgent.cs:14`), called through `EvaluateScriptAsync`. It needs no bridge. Input is dispatched DOM events (`isTrusted` false): no CSS `:hover`, no native popups (`../NeoAstra/doc/browser-automation.md`, "How it differs").
- One automation has one selected page for all its callers (`NeoAutomation.cs:29`).

**A second view in the window is possible, moving it is not exposed.**

| Need | NeoAstra 1.3.x | Evidence |
| --- | --- | --- |
| Create a view with its own rectangle, label, profile, no bridge | Yes | `NeoAstraOptions.FillParent`, `Bounds`, `ViewLabel`, `Profile`, `BridgePolicy` (`NeoOptions.cs:436-474`); two views on one window in `NeoAstra.NativeAotFixture/Program.cs:36` |
| Change the rectangle later | Native only | `neoastra_view_set_bounds` exists (`native/include/neoastra.h:292`) and is implemented for WebView2 and WKWebView; **no managed method calls it** (`NeoAstra.cs` has none) |
| Position on Linux | No | the GTK backend only sets a size request (`gtk_backend.cpp:595`); the view is an overlay child without an offset |
| Hide and show one view | No | visibility follows the window (`sync_window_view_visibility`, `windows_backend.cpp:399-408`), which also shows every view again when the window is shown |
| Give one view the keyboard | No | CodeAlta already calls `ICoreWebView2Controller::MoveFocus` through the COM table of the borrowed controller (`src/CodeAlta/Desktop/DesktopWindowFocus.cs:14,78`) |
| Script at document creation, script evaluation, capture, navigation and new-window decisions | Yes | `AddScriptAsync`, `EvaluateScriptAsync`, `CaptureAsync`, `NavigationRequested`, `NewWindowRequested`, `PermissionRequested`, `DownloadRequested` (`NeoAstra.cs:102-111,225,270,426`) |

`NeoNativeHandleKind.WebView2Controller` gives CodeAlta the controller, so on Windows `put_Bounds` and `put_IsVisible` can be called the way `MoveFocus` already is. That is the route for decision 4.

**The page of the window has no iframe browsing**, and an iframe cannot be the answer: it cannot be driven across origins and many sites refuse framing.

**The session pane.** A session is shown in a pane of its own, and things already travel with that pane: the Notes overlay "travels with that session pane" (`doc/desktop.md`, "Notes"). The browser becomes a part of that pane. Terminals are the model for host-side state with an id that the page watches (`Desktop/Terminals/DesktopTerminals.cs`, `Desktop/Rpc/TerminalsRpc.cs`, `DesktopAltaTerminals.cs`).

## Design

### 1. The browser of a session

A host service `DesktopBrowsers` (`src/CodeAlta/Desktop/Browser/`) owns, for each session, an ordered list of pages: an id, the view, its address, title, loading state and history flags, and which page is in front. It is the single writer of that state and runs on the UI thread of the window; the page hears of changes through one `browser.watch` channel, as terminals and canvases do.

- **In the session pane.** The pane of a session gets a browser part at the right of its timeline and composer, with a splitter; a button of the session's header opens and closes it (with a count of open annotations). Its top is a strip of the session's pages (title, favicon, close, **+**), then the toolbar, then the page. The width and whether it is open are kept per session.
- **Follows the session.** Only the page in front of a session that is shown has a visible native view. Another session in front hides the views of this one without ending them: scroll position, form input and sign-in are as they were on return. Two session panes side by side each show their own browser.
- **Lifetime.** Pages live as long as the session is open in the window. A session whose tab is closed, archived or deleted ends its views; the addresses of its pages are kept with the session and loaded again when it is opened, also after a restart. To bound memory, the views of sessions that have not been shown for a while are let go beyond a limit (kept: the address; lost: the state of the page); the limit is settled in the spike.
- **Sub-agents.** A child session has a browser of its own, empty until it opens a page. Nothing is shared between sessions but the profile (cookies, sign-in).

- **Isolation.** The views are created on an **environment of their own** (`application.CreateEnvironmentAsync` with `UserDataRoot = <DataRoot>/webview-browser`, no custom scheme): another browser process, other cookies and storage, and no `app://` scheme, so a foreign page can neither load nor frame the application. If a second environment in one process proves unusable (spike, step 0.3), the fallback is a named profile on the window's environment plus a navigation rule that refuses `app:`.
- **No bridge.** `BridgePolicy = Disabled`. The RPC of the window does not exist in these views.
- **Decisions.** `NavigationRequested` allows `http`, `https` and `about:blank`; everything else is cancelled (`file:`, `app:`, `javascript:` typed in the address bar). `NewWindowRequested` loads the address in a new page of the same session. `PermissionRequested` denies; `DownloadRequested` cancels in the first version, with a notice. Certificate errors are refused, with the engine's error page.
- **Browser features.** Context menu, DevTools, history navigation and zoom on: this is a browser, unlike the application shell (`NeoBrowserFeatures.ApplicationShell()`).
- **Labels.** `ViewLabel = "browser:<page id>"`; the service knows the session of each page. The window's automation gets `PageFilter = view => view.ViewLabel?.StartsWith("browser:") != true`.

### 2. Placing a native view over the page

The native view lies above everything the React page draws. The page owns the layout, the host owns the view.

- `BrowserPane` (`src/CodeAlta/frontend/src/browser/`) draws the strip of pages, the toolbar (back, forward, reload/stop, address, **Annotate**, the annotations list, DevTools) and an empty placeholder. It reports the placeholder's rectangle to the host (`browser.place`) from a `ResizeObserver` and a coalesced animation frame: CSS pixels plus `devicePixelRatio`, which in WebView2 already includes the zoom of the window's view. The host converts to the units the backend takes and verifies the result on a scaled display (step 0.2).
- **Hidden** when its session is not shown, when another page of the session is in front, when the browser part is closed, while a splitter or a tab is dragged, and while anything of the page has to appear above it: a dialog, a menu, the command palette, a tooltip that crosses it. One shell-level signal (`nativeViewOcclusion`) is raised by those components; step 1.4 inventories them. While hidden for an overlay, the placeholder shows the last capture of the view (`CaptureAsync`) so the tab does not flash empty.
- **Window show.** NeoAstra shows every view when the window is shown again; the service applies its own visibility after `NeoWindow` visibility changes.
- **Keyboard.** `DesktopWindowFocus.Follow` gives the keyboard to the window's view when the window is used again. It must give it to the view that had it last. A click in the browser view takes the focus by itself; a shortcut of the window (`Ctrl+Shift+P`, tab switching) does not reach the shell while the browser has the keyboard: the first version documents this and offers `Esc`-less ways back (click in the shell, toolbar). A later step can forward chosen accelerators.

A small interface `IBrowserViewPlacement` (set bounds, set visible, focus) hides how this is done. Windows implements it over the borrowed controller, creates the view on the raw `HWND` of the window and raises its child window once (see "Spike results"). When NeoAstra has `SetBounds`/`IsVisible`/`Focus`, one implementation over them replaces it and serves macOS and Linux. Until then the browser is offered on Windows only: elsewhere the command and `alta browser` answer `browser.unsupported`.

**Proposed to NeoAstra** (issue or pull request, not a blocker): a managed `NeoAstra.SetBounds(NeoRect)` over the existing native call, `NeoAstra.IsVisible` with a per-view flag that the window's show/hide respects, positioning of a non-filled view on GTK, `NeoAstra.Focus()`, and a statement of the units of view bounds (see "Bounds in device pixels" in `doc/open-follow-ups.md`).

### 3. Agent control

A second `NeoAutomation` on the same application, with `PageFilter` taking only `browser:` views and `NewPageHandler = DesktopBrowsers.CreateForAutomation`. The two automations never share a view. `AllowedDirectories` is a `browser` folder of the data root, with the same staging of `filePath` as the UI tools (`DesktopUiAutomation.CallAsync`).

- **Tools.** The Chrome DevTools MCP set with a `browser_` prefix (`browser_take_snapshot`, `browser_click`, `browser_navigate_page`, `browser_new_page`, …), because a session can hold the UI tools at the same time and their names would collide. `new_page` and `close_page` are included here: they open and close pages of the browser of the calling session.
- **Activation.** `alta browser activate` / `deactivate` / `status`, built like `alta ui` (`DesktopUiPlugin.cs`): the tools join the running turn and come with later runs. A built-in plugin `browser` carries the commands, the tools and one line of the instructions ("Browser: inactive…", which pages the session has open and how many open annotations).
- **A session drives its own browser only.** The wrapper lists, selects and addresses only the pages of the calling session (`list_pages` is filtered, a `pageId` of another session is "No page found"), and keeps the selected page of each session itself. Sessions cannot get in each other's way, and what an agent does happens where the user looks for it: in the browser of that session. A page the agent opens appears in the strip of its session; the browser part opens by itself only when the session is in front.
- **A session that is not shown.** Its views are hidden. Snapshots, input, console and network work there (the automation clocks itself, `browser-automation.md` "Waiting"); a screenshot needs a visible view on Windows. The spike settles whether a hidden view can be captured by keeping it placed out of sight; otherwise `browser_take_screenshot` answers that the session has to be shown.
- **Reviewed sessions.** The reason the UI tools are refused (a session could answer its own review in the window) does not apply: the browser view is not the window. What applies is the rule of terminals (`IAltaTerminals.AcceptsInput`): a session whose commands the user reviews gets the reading tools (`take_snapshot`, `take_screenshot`, console, network, `list_pages`, annotations) and is refused the acting ones (`click`, `fill`, `type_text`, `press_key`, `navigate_page`, `new_page`, `evaluate_script`, `upload_file`). Wiring acting tools into the permission review is a later step.
- **MCP server.** After the first version the desktop MCP server offers the same tools (`DesktopMcpTools`). Its caller is no session, so each call names the session whose browser it means.

### 4. Annotations

**Picking, without a bridge.** A script of CodeAlta is added to browser views at document start (`AddScriptAsync`). It does nothing until the host turns pick mode on with `EvaluateScriptAsync`. Then it outlines the element under the pointer, and a click (captured, not delivered to the page) stores a description of the element. The host polls for it on its own clock while pick mode is on, as the automation does for its jobs; `Esc` or the toolbar ends the mode.

- **The text is typed in CodeAlta, not in the page.** After a pick the browser part shows its annotations list (part of the React page, below the placeholder, which shrinks) with the element's summary, its cropped screenshot and a text field. Only **Save** there creates an annotation. A page can therefore lie about the element it reports, but cannot create an annotation or write its text.
- **What is recorded.** The address and title; a CSS selector (an `id`, `data-testid` or other stable attribute where there is one, a structural path otherwise) and the chain of ancestors; tag, role and accessible name; the visible text (shortened); the opening tag; the rectangle and the viewport size; a PNG of the element with some margin (`CaptureAsync` with a region). Second step: source hints the frameworks leave in development builds (React `_debugSource`, Vue `__file`, `data-*` source attributes), which is what finds the component fastest.
- **Markers.** For the open annotations of the address shown, the script draws numbered badges at the elements their selectors still find. They are decoration: an annotation whose element is gone stays in the list, marked "not found on this page".

**Storage.** `BrowserAnnotations`, a single-writer service over one file per session in the application state (`<StateRoot>/browser/<session id>/annotations.json`, screenshots and the list of pages beside it), so the developer instance has its own. An annotation has an id, a number, the text, the element record, `open` or `resolved`, and who resolved it with an optional note. Nothing is cleared on navigation. The folder goes with its session: deleted with it, by hand or by retention (`doc/session-retention-runtime.md`), and counted on Settings > Storage.

**How an agent gets them.** Always those of its own session.

- `alta browser annotations list [--status open|resolved|all] [--url]`, `show <id>`, `resolve <id> [--note]`, `reopen <id>`; `--session <id>` reads those of another session (a parent reading a child's, or the reverse). Text only.
- `browser_get_annotation` (among the activated tools) returns the record with its screenshot, since an `alta` command cannot return an image.
- **Add to prompt** in the annotations list puts the open annotations into the composer of the session: the texts, the element records and the screenshots as prompt images (`doc/prompt-images.md`). The user completes and sends it; nothing is sent by itself.
- The line of instructions says how many open annotations the session has, so a session asked to "fix what I marked" finds them.

**Untrusted content.** The element record is text of a foreign page. Everywhere it reaches a model it is delimited and labelled as page content ("Element as the page reports it"), apart from the user's own text. The same holds for snapshots and console messages, which already reach the model through the tools; the tool descriptions say so.

## Steps

### Phase 0: Spike (Windows, throwaway code allowed)

Done on 2026-10-13 with `src/prototypes/CodeAlta.Browser.Spike` (NeoAstra 1.3.1, WebView2, Windows 11 arm64, display at 100%). See "Spike results" below.

- [x] 0.1 A second view on the window of the main view, with a rectangle of its own, above the main view.
- [x] 0.2 Move, resize and hide it through the borrowed `ICoreWebView2Controller`; window hide/show, maximize and restore. **Not measured: a display scaled to 150% and a changed window zoom** (this computer has neither); to do in 1.1 on such a display.
- [x] 0.3 A second environment with its own user-data root in the same process.
- [x] 0.4 Two `NeoAutomation` instances with disjoint filters.
- [x] 0.5 Ten hidden views beside one shown: memory, start time, suspending, captures of views that are not shown.
- [ ] 0.6 Open the NeoAstra issue with the API of section 2 (an outward step: only when the user says so).

#### Spike results

| Question | Result |
| --- | --- |
| Parent of a browser view | `NeoAstraHost.FromWin32Hwnd(<HWND of the window>)` with `FillParent = false`. With `NeoAstraHost.FillWindow(window)` the view is always fitted to the window, whatever `FillParent` says (`NeoEnvironment.cs:137`), and shown again with it. |
| Initial rectangle | Taken as given: 500x400 at (600,100) gives a page of 500x400 CSS pixels at scale 1. |
| Z-order | **A view created later lies under the main view.** One `SetWindowPos(child, HWND_TOP, …)` on its child window (`Chrome_WidgetWin_0`) puts it above. It stayed above through a focus move to the main view, maximize, restore, window hide/show, a repaint of the main page, and hide/show of the view. Hit testing inside its rectangle gives the browser view. |
| Move and resize | `put_Bounds`: 0.2 to 0.4 ms a call; the page follows (900x400 CSS pixels). The rectangle is kept through maximize and restore. |
| Hide and show | `put_IsVisible`: 1 to 5 ms; `document.visibilityState` follows. With the raw `HWND` as parent NeoAstra does not show the view again after a window hide/show. |
| Second environment | Works: two browser processes in one application process. Its first page costs about 150 MB of private memory (browser, GPU, two utility processes and one renderer), as much as the main environment of the spike. |
| Two automations | Each lists one page and snapshots its own document only; a click, the console messages and a `confirm` dialog stay with their automation. A **hidden** view takes snapshots and clicks. |
| Capture | 80 to 125 ms for 900x400 and 1200x800. A hidden view cannot be captured ("A WebView2 view must be visible"); **a visible view placed outside the client area can**, in the same time. |
| Memory of hidden views | Ten more hidden views were created and loaded in 1.25 s. A small page costs about 27 MB of private memory a view; ten real sites (example.com, react.dev, github.com, MDN, vite.dev, twice each) about 68 MB a view on average, 829 MB for the environment. Disposing them gives the memory back. |
| Suspend (`ICoreWebView2_3.TrySuspend`) | All ten hidden views suspended; shown again in 8 ms with the state of the page kept. **Memory after five seconds: 788 MB instead of 829 MB.** Suspending stops the scripts of a page; it is not what bounds memory. |

What follows for the design:

- The placement of section 2 is feasible on Windows with NeoAstra as released: the raw `HWND` as parent, the controller for bounds and visibility, and one raise of the child window after creation (the child is the one that was not there before the view was created).
- The limit of live views stays the tool for memory, at about 30 to 70 MB a page. Suspending is optional and comes later, to quiet pages that run timers.
- A screenshot for a session that is not shown is possible: make the view visible outside the client area for the capture, then hide it again.
- The still image costs about 100 ms: it is taken when an overlay is about to cover the view, not kept up to date in the background. Step 1.4 decides whether that delay is acceptable for menus, or whether those show the plain placeholder.
### Phase 1: The browser of a session

Host side committed as `12b39169` (42 tests of the browser, the focus and the UI tools pass), the page as `de947b92` (22 tests of the browser folder pass, `tsc` clean). First look in the developer instance on 2026-10-13: see 1.7.

- [x] 1.1 `DesktopBrowsers` (pages per session, the page in front, the limit of live views), the Windows views (`WebViewBrowserViews`, `BrowserViewInterop`), the isolation and the decisions of section 1; the window's automation filter. Tests: `DesktopBrowsersTests`. **Still to do on a scaled display: the 150% check of 0.2.**
- [x] 1.2 `BrowserService` (`state`, `open`, `close`, `select`, `move`, `navigate`, `command`, `place`, `release`, `capture`, `watch`), each naming the session, and the typed client.
- [x] 1.3 The browser part of the session pane: `BrowserPane` with its strip of pages, the splitter, the command **Browser** (`/browser`, for the session in front), width and open state per session. A session pane has no header: the button is in the composer row, beside the info button. Tests: hub, placement, a `*.browser.test.ts` with a fake hub.
- [x] 1.4 Occlusion. There is no shared overlay component (about 40 popover call sites, native `<dialog>` windows, Blueprint dialogs, two toasters): one observer of the DOM raises the signal, with a rectangle test for what is not modal. The still image is taken ahead of time (when the pointer comes back from the view, and after a load), since a capture takes about 100 ms and needs a visible view.
- [x] 1.5 Keyboard: `DesktopWindowFocus` gives the keyboard to the browser view that had it last when the window is used again, and a browser view that is hidden while it holds the keyboard gives it back to the page. Not verified natively yet (1.7).
- [x] 1.6 Persistence: `BrowserFileStore`, `<StateRoot>/browser/<session id>/pages.json` (addresses, titles, order, the page in front), restored when the browser of the session is first asked for, removed when the session is deleted (`SessionRuntimeService.SessionDeleted`).
- [ ] 1.6b What the host cannot see: a session that retention removes raises no event, so its `pages.json` stays. A sweep at startup against the sessions that exist; and a `browser` line in Settings > Storage (the scan classifies by root folder: today it is "other").
- [ ] 1.7 Verify in the developer instance (`codealta-dev`). Seen on 2026-10-13 (dark theme, 2048x825 at 100%): the browser part opens from the composer row; `example.com` typed in the address field loads and is drawn exactly in the placeholder; the strip shows the title; `pages.json` is written; the tools of the window list one page only (the filter holds); another session in front hides the view (checked by the pixels of the window), back in the session it is there again; a toast that crosses the view and a modal dialog hide it, with the still image in its place; the instance exits cleanly with the second environment. Second look, after the fix `14ac7fb2`: after a restart the session tab comes back with its browser part and its page; a second page (`vite.dev`) opens in the strip, and Enter in the address field gives the keyboard to the page; the light theme; at 900x650 the browser part takes the whole pane. All .NET tests pass in Debug (7226 passed, 40 skipped). **Still to check:** two sessions side by side, a space switch, the keyboard after a switch of application, a 150% display, and that a page is not loaded again on return (covered by a unit test only).

Facts found on the way:

- The host learns of one thing only about a session's life: its deletion. It does not know which sessions are open in the window (tabs are state of the page), and nothing is raised for an archived project. `browser.release` exists for the page to say that a session is no longer shown, but the page does not call it: a click on another session replaces a preview tab, and the user's requirement is that the browser is there again as it was. The limit of live views is what bounds memory.
- A toast of the application that stays on screen (the update notice) hides a view it crosses for as long as it is there. To decide: move such toasts, or let them shrink the view instead of hiding it.
- RPC handlers run off the thread of the window. `WebViewBrowserViews.RunAsync` brings every request, and every continuation of it, to that thread; `DesktopBrowsers` is written for one thread.
- A view reports the end of a navigation, not a new title or address: the views are asked what they show every 400 ms.
### Phase 2: Agent control

Host side committed as `70b611b8` and `38c2d49a` (doc) by a child session: 66 focused tests pass; the whole .NET run had 7236 passed, 40 skipped and one failure outside this work (`AgentToolsTests.ShellCommandTool_TimesOutWhileShellIsStillRunning`, a temporary folder still in use at cleanup, which passed when run again alone). First run in a window: see 2.4.

- [x] 2.1 The browser automation (`DesktopUiAutomation.ForBrowsers`), the `browser_` tools limited to the pages of the calling session (`DesktopBrowserTools`), file staging. Tests: `DesktopBrowserToolsTests`.
- [x] 2.2 Built-in plugin `browser` (`DesktopBrowserPlugin`): `alta browser activate|deactivate|status|list|open|close`, the instruction line, the reviewed-session rule. Tests: `DesktopBrowserPluginTests`. **Not built: `alta browser show`** (it needs the page of the application to bring a session and its browser part to the front).
- [ ] 2.3 Timeline: screenshots as **Image read** cards, as for the UI tools; tool call titles. Not looked at yet.
- [ ] 2.4 Verify in the developer instance. Done on 2026-10-13 with a session that was **not shown** (model `claude-sonnet-4-6`, 12 tool calls, 43 s): `alta browser status` and `activate` (23 tools, in the running turn); `browser_new_page` with `https://example.com`; `browser_list_pages` (`1: Example Domain (https://example.com/) [selected]`); a snapshot with the heading and the link; `browser_evaluate_script` (the page is 1280x800); a screenshot of the hidden view that shows the page; a click on the link, which navigated; the console; `browser_navigate_page` to a `file:` address, refused; `alta browser list`. The pages of the session are in its own `pages.json`, beside the two of the other session; the tools of the window still list one page; the instance exits cleanly. **Still to check:** a reviewed session reads only; a JavaScript dialog by hand and through `browser_handle_dialog`; the screenshot and the still image of a session that is shown; Settings > Plugins.
Facts found on the way:

- **JavaScript dialogs.** A `NeoAutomation` takes the dialogs of every page it drives (`NeoAutomationPage.Attach`). The browser automation is always on, so a page that calls `alert`, `confirm` or `prompt` shows no dialog, also when the user browses by hand: it stays stopped until `browser_handle_dialog` answers or 30 seconds pass, then an alert is accepted and the others are cancelled. **To decide:** show such dialogs in the browser part of the pane (work of the page), answer them at once while no session with the tools drives the page, or ask NeoAstra for a pass-through.
- `browser_new_page` opens an empty page and loads it through the automation's `navigate_page`, which is what knows when a document is there.
- NeoAutomation marks `select_page` read-only and the two `take_` tools not: here the takes are reading and `select_page` is acting. A reviewed session cannot name a `filePath` for a snapshot or a screenshot.
- NeoAutomation appends the pages of every session to some results: the wrapper replaces that list with the one of the session.
- A view is created 1280x800 (hidden, out of sight) so that a page an agent drives without its session being shown has a real layout, and a page that is driven counts as used for the limit of live views.
- The browsers are created before the host, so that the plugin exists when the host lists its plugins.
### Phase 3: Annotations, and two fixes decided by the user on 2026-10-13

Built by a child session, each part seen working in the developer instance (dark theme, 100%, a local test page): `ad689917` (dialogs), `c4404f46` (toasts), `71380c46`, `2f67ee4e`, `63bf11f4` (annotations). Focused tests: 63 .NET tests of the browser and the six test files of `frontend/src/browser` pass; `tsc` clean. The whole runs, made while another worktree ran its own tests on the same computer: .NET 11 failures, all in `SessionRuntimeForwardingLifetimeTests` (timeouts at cleanup; the project does not reference the desktop code and passed 499 of 499 alone); `npm test` 1592 of 1593 (`sessionTabMenu.browser.test.ts`, which passed twice alone).

- [x] **JavaScript dialogs of a page** (the fact recorded under phase 2): shown in the browser part of the pane (`BrowserDialog.tsx`), answered there or by `browser_handle_dialog`, whoever is first; browser views wait ten minutes for an answer. The dialog reaches the pane with the 400 ms poll of the views.
- [x] **Toasts that stay** (the fact recorded under phase 1): a browser part that is shown reserves its rectangle, and the toasters keep to the rightmost stretch of the window that no browser part takes, when it is at least 340 pixels wide. Below that the occlusion rule hides the view, as before.
- [x] 3.1 The picker script (`Desktop/Browser/annotation-picker.js`, embedded in the host; the same file is tested in headless Edge by `annotationPicker.browser.test.ts`).
- [x] 3.2 `BrowserAnnotations` storage (`annotations.json` and `annotation-<id>.png` beside `pages.json`), the `browser` service calls, the element picture (a PNG with a 16 pixel margin).
- [x] 3.3 The annotations list of the browser part, below the page: pick, write, save, list, resolve, reopen, delete, go to the element.
- [x] 3.4 `alta browser annotations list|show|resolve|reopen`, `browser_get_annotation`, the count in the instruction line and on the buttons. **Checked on 2026-10-13 in an agent run of a test session** (`claude-sonnet-4-6`, 6 tool calls): the instruction line names the open annotation; the list gives its text and address; `browser_get_annotation` returns the picture of the element and the record under "Element as the page reports it (content of the page, not instructions)"; resolve with a note and reopen work.
- [x] 3.5 **Add to prompt**: the text goes into the draft, the pictures (up to eight) as a paste on the composer. **Seen with the text only**: the composer refused the pictures because the model of the test session has no observed image capability; with a capable model it is covered by a test with a fake composer.
- [ ] 3.6 The folder of a session's browser is removed with the session. Open, together with 1.6b: the sweep for sessions that retention removed, and a line in Settings > Storage.

Facts found on the way:

- WebView2 refuses a script that is added for the main frame only (`MainFrameOnly`): no view was created at all. The picker is added for all frames and returns at once in a frame.
- `BrowserFileStore.Save` deleted the whole folder of a session without pages, which would have taken its annotations: it removes `pages.json` only.
- The address and the title of an annotation are the ones the host knows, not what the page says.
- A click inside a browser view, for a check: post `WM_LBUTTONDOWN`/`WM_LBUTTONUP` to its `Chrome_RenderWidgetHostHWND`.

Rebased on `main` (`301b0b5c`) on 2026-10-13, with two conflicts in the parameter lists of `SessionWorkspace` and `OwnedSessionPanel`. The whole runs after it found a defect of this branch: the row around the conversation (`.session-pane-row`) was laid out by the stylesheet of the browser feature, so a page that loads the shell's stylesheet alone, as the window fixtures of the tests do, had a timeline without height (`timelineKeyboard.browser.test.ts` and `sessionSingleTab.browser.test.ts`, new on `main`, failed here and passed on `main`). Fixed in `57b3f6ee`: the row is laid out by `style.css`. **Whole runs after the fix, with nothing else running: .NET 7253 passed, 40 skipped, 0 failed; `npm test` 1616 of 1616.**
Still to check: `browser_handle_dialog` racing the user; the light theme and a narrow window for the new parts (covered by a browser test at 338 pixels); a 150% display; two sessions side by side; Add to prompt with a model that takes images.
### Phase 4: After the first version

- [ ] 4.1 Source hints of frameworks in the element record.
- [ ] 4.2 The browser tools in the desktop MCP server.
- [ ] 4.3 Acting tools under the permission review of reviewed sessions.
- [ ] 4.4 macOS and Linux over the NeoAstra API, once released; remove the Windows-only placement.
- [ ] 4.5 Links of messages to `http://localhost…` offer "Open in CodeAlta"; downloads; accelerators of the shell while the browser has the keyboard.

### Phase 5: A browser in a window of its own (asked by the user on 2026-10-13, after phase 2)

For several monitors: the browser of a session is taken out of its pane and shown in a window of its own.

- [ ] 5.1 Spike: move a view to another window without loading it again (`ICoreWebView2Controller.put_ParentWindow`), with NeoAstra as released. The fallback is a new view in the new window, which loads the address again.
- [ ] 5.2 First version: the window shows the page alone, and fills it; nothing of the application lies over it there, so nothing hides it. The strip of pages and the toolbar stay in the session pane. The window names its session in its title, keeps showing that session's page while another session is in front, and remembers its place and its monitor for each session. Closing the window puts the browser back in the pane.
- [ ] 5.3 Later, if the address field apart from the page is in the way: a toolbar of its own in the window. The application has one window with a page of its own today, so this is the larger step.
### Later, if needed: a DevTools connection from outside (noted for the user on 2026-10-13)

Not planned work: an option to come back to if the tools of phase 2 do not reach far enough.

- A WebView2 view can be driven both ways at once: by the automation of NeoAstra from the host (a script in the page, phase 2), and over the debugging protocol of the browser (CDP), which is what Chrome DevTools MCP speaks. They work at different levels and should not get in each other's way; **this was not tried**.
- How: a debugging port for the browser environment of the session browsers only (`NeoEnvironmentOptions.BrowserArguments`, `--remote-debugging-port`), not for the window of the application. Chrome DevTools MCP attaches to a running browser with `--browser-url=http://127.0.0.1:<port>` and sees the pages as tabs.
- What it gives: trusted input (real hover, native lists), performance traces, CSS inspection, emulation.
- What it costs: no separation by session (every page of every session is reachable over the port); any program of the computer that knows the port controls the pages and their sign-ins; a page opened from outside is probably not a page of a session; Windows only.
- Shape, if built: a switch that is off by default, the loopback address only, a random port that the window shows, open only while the switch is on. First a short trial that both ways run side by side.
### Docs, with the phase that changes behavior

- [ ] `doc/desktop.md`: a "Browser" section (tab, isolation, placement, annotations), and "UI tools" (the filter, the second tool set).
- [ ] `doc/live-tool.md`: `alta browser`. `doc/development-guide.md`: native views over the page and the occlusion signal.
- [ ] `site/docs`: a user page with screenshots (2400×1500 baseline, `doc/site-screenshots.md`); `readme.md` feature list.

## Risks

| Risk | Handling |
| --- | --- |
| A native view over the page is the classic "airspace" problem: any overlay nobody thought of is hidden behind it | One signal, an inventory (1.4), and the still image; new overlays register by using the shared dialog and menu components |
| Calling COM methods by table slot is fragile | Confined to one class beside the existing `MoveFocus` call, covered by a native smoke test, replaced by the NeoAstra API |
| A second environment doubles browser processes and memory | Created on first use of a browser page, disposed with the last one |
| A view per page per open session adds up | Hidden views beyond a limit are let go and their address reloaded on return (0.5); a session that never opens the browser costs nothing |
| A narrow session pane leaves little room beside the timeline | Below a width the browser part covers the timeline instead of sharing the pane; the splitter remembers the width per session |
| Screenshots of a session that is not shown fail on Windows | Settled in 0.5; otherwise a clear tool answer, and `alta browser show` |
| Dispatched input is not trusted: `:hover` styles, native `<select>` lists and features needing a user gesture do not react to the agent | Stated in the tool descriptions and the docs, as for the UI tools; the user can always act in the view himself |
| Page text is a prompt-injection surface | Labelled and delimited; annotations only from the user's own Save; reviewed sessions cannot act |
| Selectors go stale when the page is rebuilt | The record keeps text, role and screenshot besides the selector; "not found on this page" instead of a wrong marker |
| macOS and Linux users see no browser at first | Explicit `browser.unsupported`, documented; tracked by 4.4 |

## Verification

`dotnet build` and `dotnet test` in the configuration the running instance does not use, `npm test`, `lunet build`; each phase is looked at in the developer instance through `codealta-dev`, in both themes and at 900×650.
