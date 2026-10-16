// The entry of `lib/remote.js` (vite.config.ts): the remote server inserts it into the entry document before the page's
// own module script, so the transport exists when `@neoastra/client` looks for it. In the window of the desktop app the
// page is not served over HTTP and the host installs its own transport: nothing happens then.
import { installRemoteTransport } from "./remoteTransport";

if (location.protocol === "http:" || location.protocol === "https:") {
  installRemoteTransport({
    createSocket: url => new WebSocket(url),
    url: `${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/rpc`,
  });
}
