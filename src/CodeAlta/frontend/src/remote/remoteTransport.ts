/**
 * The key `@neoastra/client` looks under for the transport of the page (`discover()` in its `dist/index.js`). The key
 * is private to the package: `remoteTransport.test.ts` reads it from there and fails when a client update renames it.
 */
export const remoteTransportKey = Symbol.for("@neoastra/client/transport/v1");

/** The reason of the close frame the page gets when the socket closes; `main.tsx` reloads the page on it, as in the window. */
export const remoteSessionClosed = "rpc_session_closed";

/** What the transport needs of a WebSocket, so a test can give it a fake. */
export interface WebSocketLike {
  readonly readyState: number;
  readonly CONNECTING: number;
  readonly OPEN: number;
  onopen: ((event: Event) => void) | null;
  onmessage: ((event: MessageEvent) => void) | null;
  onclose: ((event: CloseEvent) => void) | null;
  send(data: string): void;
  close(code?: number, reason?: string): void;
}

/** The error `send` throws once the socket is closed, in the shape of the client's own failures. */
export type RemoteTransportError = Readonly<{ code: "connection_closed"; message: string }>;

export type RemoteTransportOptions = Readonly<{
  /** Opens the socket; `new WebSocket(url)` in the page. */
  createSocket: (url: string) => WebSocketLike;
  /** The address of the RPC socket, `ws(s)://<host>/rpc`. */
  url: string;
  /** The largest frame the transport announces to the client; the server's `MaximumFrameBytes` by default. */
  maximumFrameBytes?: number;
}>;

/** The transport object as the client reads it (`bootstrap` in `connect()`). */
type RemoteTransport = Readonly<{
  metadata: Readonly<{ handshakeTimeoutMilliseconds: number; maximumDiagnosticQueue: number; maximumFrameBytes: number }>;
  send(frame: unknown): void;
  setReceiveHandler(handler: (value: unknown) => void): () => void;
}>;

const defaultMaximumFrameBytes = 128 * 1024 * 1024;

/**
 * Installs the transport that carries the RPC frames of `@neoastra/client` over a WebSocket of the remote server
 * (`Desktop/Remote/DesktopRemoteServer.cs`), which does the `hello`/`hello_ack` handshake itself. Frames sent while the
 * socket connects are sent once it is open; after it closed, `send` throws. Received text is parsed as JSON and given to
 * the receive handler, kept until one is registered; text that is not JSON is dropped. The socket closing, for whatever
 * reason, reaches the page once as a host close frame with the reason `rpc_session_closed`. Nothing is installed, and no
 * socket is opened, when something already is under the key: the window of the desktop app has its own transport.
 */
export function installRemoteTransport(options: RemoteTransportOptions): { installed: boolean; close(): void } {
  const target = globalThis as unknown as Record<symbol, unknown>;
  if (target[remoteTransportKey] !== undefined) return { installed: false, close() {} };
  const socket = options.createSocket(options.url);
  const pending: string[] = [];
  const queued: unknown[] = [];
  let handler: ((value: unknown) => void) | undefined;
  let closeReported = false;
  const deliver = (value: unknown) => { if (handler) handler(value); else queued.push(value); };
  const reportClose = () => {
    if (closeReported) return;
    closeReported = true;
    deliver({ neoastra: 1, kind: "close", reason: remoteSessionClosed });
  };
  socket.onopen = () => { for (const json of pending) socket.send(json); pending.length = 0; };
  socket.onmessage = event => {
    if (typeof event.data !== "string") return;
    let value: unknown;
    try { value = JSON.parse(event.data); } catch { return; }
    deliver(value);
  };
  socket.onclose = reportClose;
  const transport: RemoteTransport = Object.freeze({
    metadata: Object.freeze({ handshakeTimeoutMilliseconds: 10_000, maximumDiagnosticQueue: 100, maximumFrameBytes: options.maximumFrameBytes ?? defaultMaximumFrameBytes }),
    send(frame: unknown) {
      const json = JSON.stringify(frame);
      if (socket.readyState === socket.OPEN) socket.send(json);
      else if (socket.readyState === socket.CONNECTING) pending.push(json);
      else throw { code: "connection_closed", message: "The connection to CodeAlta is closed." } satisfies RemoteTransportError;
    },
    setReceiveHandler(next: (value: unknown) => void) {
      handler = next;
      for (const value of queued.splice(0)) next(value);
      return () => { if (handler === next) handler = undefined; };
    },
  });
  target[remoteTransportKey] = transport;
  return {
    installed: true,
    /** Closes the socket and removes the transport; the page gets the close frame as for any other close. */
    close() {
      if (target[remoteTransportKey] === transport) delete target[remoteTransportKey];
      socket.close();
      reportClose();
    },
  };
}
