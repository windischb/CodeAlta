---
title: Remote access
---

# Remote access

Use the CodeAlta Desktop of one computer from a browser on another one. The sessions, the agents, the files and the git repositories stay on the computer that runs CodeAlta; the browser shows its window, with everything the window does: the Explorer, the sessions and their timelines, the composer, the code editor, the terminals, Settings.

It is for your own computers: a MacBook that builds iOS apps, used from the notebook on your desk; a workstation at home, used from a laptop in the next room. It is a prototype, and it has no login yet: read [Who can connect](#who-can-connect) before turning it on.

<figure class="alta-figure my-4" style="max-width: 44rem;">
  <img src="{{site.basepath}}/img/alta-desktop-remote-access.webp" alt="The Remote access page of Settings: the switch, the warning that there is no login, the address to listen on, the address to open and the connected windows" loading="lazy">
  <figcaption class="small text-secondary mt-2">Settings, Remote access. This developer instance uses port 2585; the normal instance uses 2584.</figcaption>
</figure>

## Turn it on

On the computer that runs CodeAlta:

1. Open **Settings**, then **Remote access**.
2. Under **Listen on**, choose the network the other computers are on: a private network such as Tailscale's is the right one. **This computer only** is for a test in a browser of the same computer.
3. Turn on **Allow other computers to use this CodeAlta**.
4. Copy the **Address**.

On the other computer, open the address in a browser. The page is the window of CodeAlta, with a **remote** tag beside its mark; hover it to see which computer it shows.

CodeAlta remembers the switch and the network: they hold when it starts again. `alta --remote-port <port>` turns it on for one start, and `alta --remote-host <address>` names the address for one start.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-remote-window.webp" alt="The window of CodeAlta Desktop in a browser tab: the remote tag beside the mark, the Explorer and a session" loading="lazy">
  <figcaption class="small text-secondary mt-2">The same CodeAlta in a browser on another computer.</figcaption>
</figure>

## What a remote window does differently

The browser is not the native window, and it is on another computer. The page knows it and leaves to the computer that runs CodeAlta what belongs there:

- **Folders**: **Add a project folder** opens the Open project window, where you type the path on the computer that runs CodeAlta; there is no folder dialog.
- **Open in \<editor\>** and **Open in the file manager** do nothing in a remote window: the programs would open on the other computer. The code editor and the terminals of CodeAlta work as usual.
- **Exit** does not end CodeAlta: exit it from its own window. Closing the browser tab leaves CodeAlta and its sessions running.
- **Zoom** zooms the page in the browser; the native window keeps its own zoom.
- The settings are those of the computer that runs CodeAlta: its providers, its external editor, what closing its window does.

A reload of the page opens a new connection. When the connection is lost (the computer sleeps, the network changes), the page says **CodeAlta is not responding** and loads itself again once it answers.

Both windows can be used at the same time: a session created in one appears in the other; a prompt sent from the browser runs on the computer that runs CodeAlta.

## Who can connect

There is no login yet. Whoever reaches the address has your control of CodeAlta: the shell, the files, the providers you signed in to. What CodeAlta does to keep that acceptable:

- Off unless you turn it on, and on **This computer only** unless you choose a network.
- A page of another web site cannot drive it from your browser: a request that does not come from the address itself is refused.
- At most eight remote windows at once.

Use it on a private network such as [Tailscale](https://tailscale.com/), where only your own devices are, and turn it off when you do not need it. Do not choose **All networks** on a computer that is reachable from a network you do not control.

The page is served over plain HTTP. On a network address, the browser then treats it as insecure and keeps some things from it: the clipboard button of a copy field, notifications and the microphone of dictation. TLS and a login are planned.
