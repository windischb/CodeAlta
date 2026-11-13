---
title: Built-in browser of a session (macOS), parked
status: parked
created: 2026-11-13
summary: Where the work on the browser built into CodeAlta Desktop stands, why it was put on hold, and how to resume it.
---

# Built-in browser of a session: parked

## What this branch has

- Branch `parked/embedded-browser-macos`: 12 commits of the built-in browser on `upstream/main` 5a3143ffc (rebased 2026-11-07), then this parking commit with the plans.
- What it does:
  - The browser of a session is a tab of the dock view, beside the timeline, on macOS only.
  - It is a WKWebView through NeoAstra 1.12.
  - A third driver, `WebKit`, sits beside CDP and BiDi, for the agent's tools.
  - It shows the CodeAlta toolbar for annotations and the JavaScript dialogs of a page.
  - Sandboxed sessions stay out of it.
  - It has a page in the user guide (`site/docs/browser.md`).
- The plans: [2026-11-05-embedded-browser-spike.md](2026-11-05-embedded-browser-spike.md) (the questions answered on macOS) and [2026-11-05-browser-tab.md](2026-11-05-browser-tab.md) (the implementation, done).
- Windows: the older branch `feature/embedded-browser` of the fork (WebView2), last commit 2026-10-14, is not rebased.

## State at parking

- The Release build had no warnings.
- `npm test` and `lunet build` were green.
- In `dotnet test`, only `AutomationRunTriggerTests.ARunOfAnotherAutomation_StartsTheAutomationThatFollowsIt…` failed. It also fails on `upstream/main` alone, about 2 runs in 5.
- It was not tried in a developer instance after the rebase.
- It was never pushed as a pull request, and the maintainer was not asked.

## Why it was put on hold

What it would bring:
- The page and what the agent does in it sit beside the timeline, with no second window.
- Annotating happens in one window.
- The tabs belong to the session.
- Nothing has to be installed on a Mac.
- The other coding tools have one by now: Cursor (Design Mode), Windsurf/Devin Desktop (Send element), VS Code with Copilot (comments per element) and Claude Desktop (Browser pane).

Against it:
- **Weaker evidence for annotations.** WebKit gives no style rules and no creation stack of an element, so the source lookup has only text, selector and address.
- **Fewer tools for the agent.** There is no trace, heap snapshot, network or device emulation, full-page screenshot, upload or download.
- **Narrow reach.** It is macOS only, and it renders with WebKit, not Chrome.
- **Interop around NeoAstra.** One class reaches the borrowed `WKWebView` through the Objective-C runtime; NeoAstra is the maintainer's own.
- **Upkeep.** A third driver must follow every change of the browser interfaces. Two new members came within a week: `FrontPagesAsync` and `ViewPictureAsync`.
- **Size.** About 4700 lines in 57 files.

## Before resuming

1. Ask xoofx in an issue whether he wants a built-in browser. Ask too whether NeoAstra should place, hide and focus a native view and give an isolated world with a channel, so that the Objective-C class goes away.
2. On Windows, look at WebView2's own CDP (`CallDevToolsProtocolMethodAsync`). It would let the existing CDP driver work with full evidence there. Neither NeoAstra nor `feature/embedded-browser` uses it yet; this is not checked.
3. A cheaper step that does not need the built-in browser: place the external Chrome window beside the CodeAlta window (`Browser.setWindowBounds` over CDP).

## How to resume

```sh
git fetch origin upstream
# the 12 commits, without this parking commit (the plans stay readable on the parked branch)
git switch -c alta/embedded-browser origin/parked/embedded-browser-macos~1
git rebase --onto upstream/main 5a3143ffc
```

Expect to implement new members of `IBrowserContext`, `IBrowserPage` and `IAnnotatablePage` in `src/CodeAlta/Desktop/Browser/WebKit/`. The rest of the upstream browser plugin moves fast too, for example the sign-ins with vault items.
