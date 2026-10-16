---
title: Remote access to a running CodeAlta from another computer
status: in-progress
created: 2026-10-16
summary: Lets the CodeAlta that runs on one computer (the Mac, for iOS work) be used from a browser on another computer, through an optional listener inside the running application, without authentication for now (loopback or a private network such as Tailscale; pairing, tokens and TLS come later); the same program, no separate server process; a headless start and the desktop app as a client come later.
---

# Remote access to a running CodeAlta

## Why

The user works on three computers (an x64 notebook, an ARM64 notebook, a MacBook that iOS needs). Each runs
its own CodeAlta, so the user has to remember on which computer a session runs, and nothing can be copied
between them. The wish: sit at one computer and use the CodeAlta of another one, with its sessions, its
agents and its files.

Two ways were weighed: run only the *agent* (the `claude` process) on the Mac through SSH, or run the whole
CodeAlta on the Mac and use it from elsewhere. The second was chosen:

- it works for every provider: an API provider (OpenAI, Anthropic, Mistral…) runs its tools (shell, files)
  in the CodeAlta process, so the process has to be on the Mac anyway;
- the Mac runs a full CodeAlta already, and the user wants to keep that;
- the files, the git state, the transcripts and the provider logins stay where the sessions run.

Constraints the user set:

- **One program, no separate server process.** The application must be running for a remote client to
  connect; the listener lives inside the running `alta`. No daemon to install, start or stop.
- Installed and managed from the ordinary application: a switch in Settings, not a second product.
- A headless start (`alta --headless`) may come later, as the same program without a window, for a user who
  wants it. It is not part of the first delivery.

## What exists (evidence)

- **A Kestrel server already runs inside the app**: the MCP server
  ([DesktopMcpServer.cs](src/CodeAlta/Desktop/Mcp/DesktopMcpServer.cs), [DesktopMcpEndpoint.cs](src/CodeAlta/Desktop/Mcp/DesktopMcpEndpoint.cs)),
  with `ModelContextProtocol.AspNetCore`, loopback by default, a `Host`/`Origin` check, an access token when
  other computers can reach it (`mcp_token.txt`), a switch and status in Settings (`frontend/src/mcpHost/`),
  `mcpServer` in `preferences.json`. The pattern (endpoint record, status record, `StartAsync`/`StopAsync`,
  `remember`) is the model for the remote listener. See `doc/desktop.md`, "MCP server".
- **The RPC host is transport-neutral**: `NeoRpcHost.OpenSession(NeoRpcSessionIdentity, NeoRpcSendFrame)`
  opens a document session with a send callback of the caller's, and `NeoRpcSession.ReceiveAsync(json)` feeds it
  frames (NeoAstra 1.3.1, `NeoAstra.xml`). The window's `NeoRpcViewBinding.Bind` is one user of that API; a
  WebSocket connection can be another, on the **same** `NeoRpcHost` that `builder.Build()` returns in
  [DesktopApplication.cs](src/CodeAlta/Desktop/DesktopApplication.cs#L834): the same 50 services, the same limits.
- **The frontend's transport is one private object**: `@neoastra/client` finds it at
  `globalThis[Symbol.for("@neoastra/client/transport/v1")]` (`dist/index.js`), and the generated bindings
  (`obj/neoastra/neoastra.ts`) only use `invoke`/`subscribe`/`invokeChannel` of the package. A page that
  installs a WebSocket transport under that key before the app module loads runs the unchanged frontend.
  The handshake (`hello`/`hello_ack`, the `close` and `diagnostic` kinds) is the package's; `runtimeInfo.backend`
  is `webview2 | wkwebview | webkitgtk` today.
- **The window is assumed to be one**: `owned` is `mutation.epoch === status.hostEpoch`
  ([main.tsx](src/CodeAlta/frontend/src/main.tsx#L2044)); the epoch is per host start, so every RPC session
  of the host is "owned". `hostLiveness.ts` pings every 10 s; `rpc_session_closed` reloads the page.
  `windowChrome.tsx` uses NeoAstra's `createDesktopClient` (native caption buttons);
  `desktopShell` has `pickFolder`, `revealEntry`, `zoom`, `hide`, `exit`; `externalEditors.openFolder`,
  terminals and canvases are RPC channels (fine over a socket). The UI tools (`alta ui activate`,
  [DesktopMcpServer.cs](src/CodeAlta/Desktop/Mcp/DesktopMcpServer.cs)) drive *the* window.
- **CSP** of the app page: `connect-src 'self'` (`neoastra.json`). A page served from the listener's origin
  with `wss:` to the same origin stays within `'self'`.
- **Remote Control** (`doc/desktop.md` "Running sessions") is Claude Code's own link to claude.ai, per
  session; unrelated to this, and not a substitute.
- NeoAstra runs on Windows, macOS and Linux (six RIDs, `doc/desktop.md` "Platforms"), so the Mac runs the
  desktop app, not only the TUI.

## Decisions

1. **Host in the running app.** `Desktop/Remote/DesktopRemoteServer` is a second Kestrel server (or a second
   endpoint of the MCP server's; the spike decides), started by a switch in Settings, stopped with it or with the
   app. Port of its own (`2584`, `2585` for `--dev`, a free one on explicit roots), `--remote-port`/`--remote-host`
   like the MCP options.
2. **Same RPC, over WebSocket.** The listener serves the built frontend (the manifest assets the window loads,
   `NeoManifestResourceProvider`) over HTTPS and one WebSocket endpoint `/rpc` that is one `NeoRpcHost` session
   per connection: the frames of `@neoastra/client`, unchanged. No second API.
3. **Browser client first.** The other computers open `https://<mac>:2584/` in a browser (or install it as a
   PWA). A "Connect to another CodeAlta" mode of the desktop app is a later phase: it is the same page
   loaded from that address in a view of its own, so phase 1 makes it possible without doing it.
4. **Off by default; no authentication for now.** Nothing listens until the user turns it on. The
   prototype has no login, no pairing and no TLS: whoever reaches the port has the user's control of the
   computer (shell, files, providers). What keeps that acceptable while it lasts:
   - the listener binds to the loopback address unless the user names another one, as the MCP server does
     (`--remote-host`, later the bind choice in Settings); the Settings page says in plain words what an open
     address means;
   - the recommended network is a private one such as Tailscale (encrypted, only the user's own devices);
     a port open on a LAN or the Internet is the user's decision and is warned about;
   - the cheap checks of the MCP server are kept: `Host` must be an address the server listens on, `Origin`
     of the upgrade must be the listener's own origin (so a web page the user visits elsewhere cannot drive
     the host), rate limits of `NeoRpcOptions`, a connection cap, a log entry per connection.
   Authentication (pairing, per-device tokens, TLS) is designed once the prototype has shown the feature is
   worth it; the sketch is kept under "Later" so nothing in phase 1–4 makes it harder.
5. **Every client is a full window of the same user.** No viewer role in phase 1: the host already serves one
   RPC session per document and events per subscription, so two windows see the same host. What breaks with two
   windows is found by an audit (phase 4), not assumed.
   - **Nothing is "synchronized"** between the windows: the state lives in the host, each window reads it and
     subscribes to its changes, as a reloaded window does today. Only window-local state stays local (open
     tabs, drafts, theme, zoom), on purpose.
   - **A remote connection is a window of one host, not an attachment to single sessions.** The remote client
     shows the whole CodeAlta of that host: its projects, sessions, spaces and settings. Three forms were
     weighed: (A) a browser tab per host — the prototype; (B) the desktop app as a client, the same page of a
     host loaded in a view of its own, reached through the **space switcher** ("MacBook" beside "Work" and
     "Personal": the window shows one space at a time, and one host at a time is the same rule); (C) one
     client that lists the sessions of several hosts together. A is now, B is the next step, **C is
     rejected**: the frontend is built around one host and one epoch, and every service call would need a
     host context.
   - **A remote host is not a space of the catalog.** A space is a group of projects inside one catalog,
     a session belongs to one for life and agents are told it; a remote host is a connection the window
     knows (name, address, later a token), kept in the desktop preferences. No `alta space` entry, no
     journal field, no **Move to space… → MacBook** (that would copy files between computers: C again).
6. **The frontend learns one thing: "I am remote."** `runtimeInfo.backend === "remote"` (or a flag the
   transport sets) hides or replaces what only the native window can do; everything else is the same code.
7. **Headless is the same binary**, later: `alta --headless` runs the host and the listener without a window;
   `--exit` and the tray icon end it. Not in this plan's checklist beyond the note.

## Phases

### Phase 0 — Spike: prove the transport (1–2 days, throwaway allowed)

- [x] In `CodeAlta.Desktop.Tests`, open a session with `NeoRpcHost.OpenSession` on a host built with
      `BootService` only, pump frames through an in-memory pair, and get `boot.status` back
      (`DesktopRemoteServerTests.RpcHost_AnswersAnInvoke_ThroughASessionOpenedWithACallback`). The
      identity that works: `ViewLabel`, `DocumentSessionId`, `ContractHash = NeoRpcGeneratedContract.Hash`,
      `SourceOrigin` of the listener, `IsMainFrame = true`, `WholeViewTrust = false`, `ProtocolMinor = 0`,
      `Features = ["invoke", "cancel", "events"]`. `NeoRpcSendFrame` returns a `ValueTask`. An invoke's
      `ReceiveAsync` completes with the result: frames are passed on without waiting for one another.
- [x] The page with the transport, served by a Kestrel server of its own
      (`Desktop/Remote/DesktopRemoteServer.cs`, started by `--remote-port <port>` on the address of
      `--mcp-host`): `GET /` is the entry document of the manifest assets with
      `<script src="/_remote/transport.js">` inserted after `<head>` (a classic script runs before the
      module script), `GET /rpc` the WebSocket. **Done 2026-10-16:** `alta --dev --remote-port 2585` and
      `http://127.0.0.1:2585/` in a Chromium page show the workspace of the developer instance — DEV tag,
      the project, its two sessions and its chat in the Explorer, the composer, the toasts — and a reload
      opens a new session (log: connected, disconnected, connected). Console: only `favicon.ico` 404.
      Tests: `Server_ServesThePageAndAnswersAnInvokeOverTheSocket` (page, script, asset, 404, foreign
      `Origin` → 403, hello/hello_ack, invoke, close handshake), `Server_RefusesAnotherProtocol`.
- [x] **NeoAstra is not changed for the prototype.** Confirmed by the spike. Everything needed is public API or CodeAlta's own code:
      - host: `NeoRpcHost.OpenSession(identity, send)` and `NeoRpcSession.ReceiveAsync(json)`; the transport
        handshake that `NeoAstra.Core` does for a WebView is done by our server instead: the client sends
        `{neoastra:1, kind:"hello", protocol:{major:1, minor:0}, features:[…]}`, the server answers `hello_ack`
        with `protocol`, `features` (`invoke`, `cancel`, `events`), `runtime` and `limits`; every other kind
        (`invoke`, `subscribe`, `cancel`, `channel_ack`, …) is passed to `ReceiveAsync` as is; a `close`
        frame ends the session; `kind:"close"` with a reason is sent back when the host closes it.
      - browser: an object under `globalThis[Symbol.for("@neoastra/client/transport/v1")]` with `metadata`
        (`handshakeTimeoutMilliseconds`, `maximumDiagnosticQueue`, `maximumFrameBytes`), `send(frame)` and
        `setReceiveHandler(fn)` returning an unregister function; `discover()` checks nothing else.
      - `parseRuntimeInfo` accepts only a true `platform`/`backend` pair (`macos`/`wkwebview`, `windows`/
        `webview2`, `linux`/`webkitgtk`): the server reports the host's own pair. "This window is remote" is
        `viewLabel === "remote"`, which the server sets; no new `backend` value is needed.
      - the symbol is private: a test reads the key from `dist/index.js` and fails when a client update
        renames it. A public `connect({ transport })` in NeoAstra is asked for only once the prototype has
        shown that the whole thing is worth it (see "Later").
- [x] Decided: **two** Kestrel applications. The MCP server keeps its "no browser pages" guard and its
      own port; the remote server has `2584`/`2585`, reuses `DesktopMcpGuard.Check` for `Host`/`Origin`
      and the quiet lifetime, and nothing else of the MCP server's.
- Seen in the spike, for phase 4: `window.isSecureContext` is true on `127.0.0.1` (a loopback origin is
  secure without TLS); over a LAN address it is not. The native caption buttons of `windowChrome.tsx` are
  not shown in the browser (nothing broke). The page asks for `/favicon.ico`, which the manifest does not
  have: serve the icon or ignore.

### Phase 1 — Listener and transport in the app

**Done 2026-10-16** (branch `feature/remote-access`). Verified: `DesktopRemoteServerTests` (8) with the MCP and
terminal-start tests, `npm test` (1797, one fixed: the browser demo stands in for `remoteHost`), the Debug build
emits `assets/lib/remote.js`, and `alta --dev --remote-port 2585` in a Chromium page loads the transport module
and shows the workspace with no console error.

- [x] `Desktop/Remote/DesktopRemoteEndpoint.cs`: host, port, chosen, like `DesktopMcpEndpoint`; defaults
      `127.0.0.1:2584` (`2585` for `--dev`, a free port on explicit roots); `--remote-port` (turns the server
      on) and `--remote-host` in `DesktopCommandLine`, handed on by `DesktopTerminalStart`.
- [x] `Desktop/Remote/DesktopRemoteServer.cs`: Kestrel of its own; `GET /` and the assets from the manifest
      provider (no CSP header yet: the page's CSP is applied by the custom scheme only — phase 4 decides);
      `GET /rpc` → one `NeoRpcHost.OpenSession` per socket (`ViewLabel = "remote"`, `WholeViewTrust = false`);
      `hello`/`hello_ack` by the server; frames to `ReceiveAsync` without waiting for one another; the close
      handshake with `CloseOutputAsync` and a 3-second patience (a page that does not answer is not waited
      for); `Status` (`running`/`stopped`/`failed`, url, error, clients) with `Changed`; `SetEnabledAsync`;
      a taken port of the application's choice is replaced, one the user named is not; at most 8 windows;
      `favicon.ico` → 204. Kestrel's own ping keeps the socket alive.
- [x] The server exists for every start that opens the window (`DesktopApplication`), on unless
      `--remote-port` is absent; the RPC host of the workspace is given to it after `builder.Build()`. The
      configuration-recovery page and the "starting" page: not done — before the host exists `/rpc` answers
      503 and the page shows **CodeAlta is not responding** with **Reload**, which is enough for now.
- [x] `frontend/src/remote/remoteTransport.ts` (`installRemoteTransport`, testable with a fake socket),
      `remoteMode.ts` (`isRemoteWindow`, `remoteHostName`), `remote.ts` (the entry; installs only over
      `http(s):`); Vite emits it as `lib/remote.js` (`codealta:remote-transport` plugin); the server inserts
      `<script type="module" src="./lib/remote.js"></script>` before the page's own module script
      (`DesktopRemoteServer.WithTransport`). A test pins the client's private symbol to `dist/index.js`.
- [x] `remoteHost.status`/`setEnabled` (`Desktop/Rpc/RemoteHostRpc.cs`, modeled on `McpHostRpc.cs`): `state`,
      `enabled`, `url`, `error`, `clients`; registered in both builders, unavailable on a catalog copy; the
      switch is not kept across starts (phase 3).
- [x] Tests: MSTest for the command line and help, the endpoint, the in-memory session, the server end to end,
      the protocol refusal, off/on and the port fallback, the page's service, the script insertion;
      `remoteTransport.test.ts` (8) and `remoteMode.test.ts` (2). A `*.browser.test.ts` that loads the page
      through the transport against a fake socket: not done — the live check in the developer instance
      stands for it until phase 4 touches the page.
- [x] `doc/desktop.md`: **Remote access (prototype)**.

### Phase 2 — The guards that cost nothing (no authentication yet)

- [ ] **Bind address**: loopback unless named; `--remote-host <address>` as `--mcp-host`; later a choice in
      Settings — *This computer only* (loopback), *One network* (a list of the interfaces with their addresses;
      Tailscale's `100.x` shows up here), *All networks*. Anything but loopback shows the warning.
- [ ] **Checks the MCP server already does**: `Host` must be an address the server listens on; `Origin` of the
      upgrade must be the listener's own origin; no CORS. Rate limits from `NeoRpcOptions`; a connection cap (8).
- [ ] **Logging**: `remote_connected` (address, user agent), `remote_refused` (why), `remote_closed` in
      Application Logs.
- [ ] Notes in `doc/desktop.md`: there is no login; whoever reaches the port has the user's control of the
      computer; use it on a private network (Tailscale) only; turn it off when not needed (the preference is
      kept across restarts and updates, as the user set it).
- [ ] Tests: Host/Origin refusal, connection cap, loopback default, a named address.

### Phase 3 — Settings page "Remote access"

- [x] `frontend/src/remoteHost/RemoteHostSettings.tsx` + `remoteHost.ts` + `.css` + `remoteHost.test.tsx` +
      `messages.ts`, in the group Advanced after **CodeAlta MCP**: the switch (**Allow other computers to use
      this CodeAlta**), the warning while it is on (no login; a private network such as Tailscale; turn it off
      when not needed), **Listen on** (the bind choice of phase 2: this computer only, each network by the name
      of its interface, all networks — with a note when other computers can reach it), why the server failed,
      the address with a copy button, **Connected windows: n**. The QR code of the first version was removed
      the same day: the other computers are notebooks without a camera (the plan says a phone is not a target);
      it comes back with pairing, if a tablet is to scan the pairing code and the fingerprint.
- [x] Preferences: `remoteAccess` (bool) and `remoteHost` (address) in `preferences.json`
      (`DesktopPreferences`, `DesktopShell.SetRemoteAccess/SetRemoteHost`); off and the loopback address are
      not written. `DesktopRemoteEndpoint.For(options, preferredHost)`: the command line first, then the
      preference, then the loopback address; `--remote-port` turns the server on for one start.
- [x] `DesktopRemoteServer`: `Bind` in the status, `SetEnabledAsync` and `SetBindAsync` remember their choice,
      a running server listens again on a new address (a free port is taken again when the port was 0).
      `remoteHost.setBind` and `addresses` (`RemoteHostService.Addresses()`: loopback, the IPv4 of each
      interface that is up, `0.0.0.0`). Tests: preferences round trip and the endpoint's precedence, the
      change of address while running and off, the refusal of a bad address, the order of the addresses.
- [x] Localization: `remoteHost/messages.ts`, five languages.
- [x] Live check in the developer instance (2026-10-16): the page lists the real interfaces (WiFi, two vEthernet,
      all networks); the switch starts the server on 2585 and writes `"remoteAccess":true`; **Listen on** WiFi
      writes `"remoteHost":"10.0.0.37"`, the server listens on that address (`Get-NetTCPConnection`), the page
      shows the note, the new address and the QR code; back to this computer and off, the file is
      `{"onClose":"ask"}` again and the port is free. `npm test`: 1802 passed, 0 failed, two browser tests cancelled
      by a stalled machine (35-minute timeouts) and green alone.

### Phase 4 — What a remote window can and cannot do (audit, then remote mode)

**Done 2026-10-16.** Verified: `tsc`, the node tests of `remote/`, `externalEditors`, `spaces` and `WindowZoom`,
`npm test` (1800, one browser test timed out under load and passes alone: `windowFrame.browser.test.ts`), the
Debug build, and the live checks on `alta --dev --remote-port 2585`: in a Chromium page the **remote** tag names
the host, there are no caption buttons and no `data-neoastra-*` attributes, **Add a project folder** opens the
Open project window to type the path (no native dialog on the host), Ctrl+= zooms the page to 110 % and the
title bar shows it, Ctrl+Q shows the "exit from its own window" toast; the native window (driven through
`codealta-dev`) has its title bar and no tag. Two windows: a session created through the host's `alta`
appeared in both (the shell's notices are broadcast to every watcher); what differs is window-local only (the
expanded projects, the zoom).

- [x] Audit of `frontend/src` (grep of `desktopShell.*`, `externalEditors`, `createDesktopClient`, `appUpdate`,
      `speech`, `projectFiles.reveal/openExternal`, `worktrees.open*`, `navigator.clipboard`, `window.open`): the
      native surface is small — `desktopShell.exit/hide/zoom/pickFolder/revealEntry`, the external opener
      (`openFolder/openEntry/openWorktree`), the title bar of `windowChrome.tsx`, and the shell's notices.
      Speech captures the microphone in the page (`getUserMedia`), so it works in a browser; the clipboard is
      the browser's; notifications are the host's (none in the page).
- [x] `remoteMode.ts`: `remoteWindowHost(runtimeInfo, location)` from the connection (the source of truth,
      `viewLabel === "remote"`), and `remoteDocumentHost()` from the mark `lib/remote.js` leaves on the root
      element (`data-codealta-remote`) for what cannot wait for the connection or must not import
      `@neoastra/client` (the node tests resolve that package to its `.d.ts` through `tsconfig` paths: a module
      they load must not import it — `SpaceIdentity.tsx` learned that). `App` keeps `remoteHost`.
- [x] `windowChrome.tsx`: the title bar is attached only when the window is not remote; `WindowBrand` has the
      **remote** tag (`.window-brand-remote`, accent color) with the host on hover.
- [x] `desktopShell.pickFolder`: `canPickFolder` is false in a remote window (**Add a project folder** → the Open
      project window, typed path with completion); the **Browse…** of the worktree folder and of the GitHub
      profile folder are not shown.
- [x] `revealEntry`/`openFolder`/`openEntry`/`openWorktree`: a remote window does not call the host; the
      opener answers `remote` and the toast says the programs would open on the computer that runs CodeAlta
      (`failure("remote")`, `remote/messages.ts`).
- [x] `exit`: a toast, no call; `hide`: unreachable (the close question is the native window's). The shell's
      notices `exit-requested`, `confirm-exit`, `confirm-close`, `close-tab` are ignored in a remote window.
- [x] Zoom: a remote window zooms its page (`document.documentElement.style.zoom`) by the host's steps
      (`nextZoom`); the title bar shows `remoteZoom`; the native window's zoom is untouched.
- [x] Two windows at once: checked with a session created through `alta` of the host (both windows list it).
      Not checked yet, for a later pass with real sessions: a prompt sent from each window to the same session,
      an ask answered in one window, rename/archive/delete from both.
- [x] Reconnection: a reload (and any socket close) opens a new session, as in phase 1; a dropped socket shows
      **CodeAlta is not responding** and reloads when the host answers (the `rpc_session_closed` path).
- [ ] Not done, left for the audit's second pass: `appUpdate.install` from a remote window (restarts the host:
      warn first); file drag-and-drop from the browser into the composer; a CSP header for the page served over
      HTTP (the custom scheme applies it in the window; the remote page has none); a phone-width layout.
- [ ] Narrow screens: not checked beyond the existing 900×650 tests of the window.

### Phase 5 — Documentation and release

- [ ] `doc/desktop.md`: a **Remote access** section (transport, address, pairing, devices, what a remote window
      cannot do, security notes), and the **Settings** list; `doc/development-guide.md` for the second server;
      `site/` user page with screenshots (`doc/site-screenshots.md` sizes); `readme.md` one line.
- [ ] `AGENTS.md`: how to test with `--dev` (port 2585) from a browser on this computer.
- [ ] Both themes, narrow window, screenshots of the Settings page and of a remote window.

### Later (not in this plan's checklist)

- **Authentication**, designed once the prototype has proven the feature. The sketch, so that phases 1–4
  leave room for it: TLS with a certificate the app generates once (`remote_cert.pfx`, DPAPI/0600, SHA-256
  fingerprint shown in Settings and in the QR code; plain HTTP on loopback only). A self-signed certificate
  is enough: once the browser's exception is accepted, the page is a secure context (clipboard, notifications,
  microphone work) and `wss:` to the same host and port works in Chromium and Safari; Firefox needs the
  exception on that exact host and port, and a PWA install is not offered without a valid certificate.
  Rules so that Safari and iOS accept it at all: validity at most 825 days (renewed by CodeAlta before it
  ends; the fingerprint then changes), `subjectAltName` with every name and address the host is reached by
  (hostname, `.local`, the Tailscale `100.x` address and `<machine>.<tailnet>.ts.net`), EKU `serverAuth`,
  RSA 2048 or ECDSA P-256. Option beside it: a certificate the user drops in (`remote_cert.pem` +
  key), such as the Let's Encrypt one `tailscale cert <machine>.<tailnet>.ts.net` gives, which removes the
  warning and allows the PWA install. Then pairing of each device with
  a 6-digit code valid 2 minutes (`POST /pair` → a per-device token of 32 random bytes kept in the browser's
  storage, hashed in `remote_devices.json` on the host with name, created, last seen, user agent; 5 failed
  codes close pairing); every `/rpc` upgrade carries the token (`Sec-WebSocket-Protocol` or an origin-scoped
  cookie), else 401; a device list with **Revoke** in Settings; `remote_paired`/`remote_revoked` log entries,
  never the token. Where it plugs in: the upgrade handler of `DesktopRemoteServer` (one check before
  `OpenSession`), one page before the app loads (the pairing form), one section of the Settings page.
- `alta --headless`: host + listener without a window, tray icon or `--exit` to end; a user who wants the
  server-like setup gets it from the same program.
- The desktop app as a client (form B): the remote hosts the window knows are entries of the **space
  switcher** ("MacBook", with the icon and color the user gives them, kept in `preferences.json`, not in the
  catalog); choosing one loads that host's page in a view of its own (the device token kept by that view's
  storage) and the switcher leads back. **Settings → Remote hosts** adds, renames and removes them.
- A public `connect({ transport })` and a `backend: "remote"` value in NeoAstra (`../NeoAstra`, xoofx), once
  the prototype has proven the feature; until then CodeAlta uses the private symbol and tests its name.
- A viewer role (read-only pairing) if sharing a screen with a colleague becomes a wish.
- Discovery on the LAN (mDNS) — the QR code and Tailscale names make it unnecessary for one user.

## Risks and open questions

- **Private transport symbol.** The prototype relies on a key `@neoastra/client` does not document, and on the
  shape of `hello`/`hello_ack` read from `dist/index.js` (protocol 1.0). A test pins both; a NeoAstra update
  that changes them breaks the remote page, not the window. The public option is asked for later.
- **One host, several documents.** NeoAstra's host is built for it (limits are per session), but CodeAlta's
  code was written for one window. The audit in phase 4 is the real cost of this plan; estimate it after the
  spike, when a second window can be opened at all.
- **No authentication.** Until the later phase, the port is the whole protection. Loopback by default and the
  warning on any other address are the guards; the docs recommend a private network (Tailscale: no port
  forwarding, stable names, encrypted already). **Secure-context Web APIs** (clipboard write, notifications,
  microphone) need `https:` or `localhost` in browsers: over plain `http://100.x.y.z:2584/` some of them are
  unavailable. Phase 4 lists which ones hurt; TLS comes with the authentication phase, or earlier if they do.
- **Mac sleep.** A sleeping Mac ends the sessions' processes as it does today; nothing new, but it is what
  the user will meet first. Document; `caffeinate`-style keep-awake is out of scope.
- **Frame cap.** Images and large tool outputs cross a socket now; the 128 MiB cap stays, but a slow link
  makes a big paste slow. Nothing to do unless it hurts.
- **Which configuration runs where.** The remote window reads the host's `config.toml`, providers, skills:
  all shared, nothing to sync. Only the browser-local state (theme, drafts, open tabs) is per client, as it is
  per WebView today.
