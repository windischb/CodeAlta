import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { installRemoteTransport, remoteTransportKey, type WebSocketLike } from "./remoteTransport";

const CONNECTING = 0;
const OPEN = 1;
const CLOSED = 3;

/** A socket under the test's control: it opens, receives and closes when told. */
class FakeSocket implements WebSocketLike {
  readonly CONNECTING = CONNECTING;
  readonly OPEN = OPEN;
  readyState = CONNECTING;
  onopen: (() => void) | null = null;
  onmessage: ((event: { data: unknown }) => void) | null = null;
  onclose: (() => void) | null = null;
  readonly sent: string[] = [];
  closeCalls = 0;
  send(data: string) { this.sent.push(data); }
  close() { this.closeCalls++; }
  open() { this.readyState = OPEN; this.onopen?.(); }
  receive(data: unknown) { this.onmessage?.({ data }); }
  end() { this.readyState = CLOSED; this.onclose?.(); }
}

type Transport = { metadata: Record<string, number>; send(frame: unknown): void; setReceiveHandler(handler: (value: unknown) => void): () => void };
const installed = () => (globalThis as unknown as Record<symbol, Transport | undefined>)[remoteTransportKey];
const closeFrame = { neoastra: 1, kind: "close", reason: "rpc_session_closed" };

function install() {
  let socket!: FakeSocket;
  let url = "";
  const result = installRemoteTransport({ createSocket: address => { url = address; socket = new FakeSocket(); return socket; }, url: "ws://127.0.0.1:2585/rpc" });
  return { socket, url, ...result };
}

test("the transport is installed under the client's key with the metadata the client reads", () => {
  const { close, installed: ok, url } = install();
  try {
    assert.equal(ok, true);
    assert.equal(url, "ws://127.0.0.1:2585/rpc");
    const transport = installed();
    assert.ok(transport);
    assert.deepEqual(transport.metadata, { handshakeTimeoutMilliseconds: 10_000, maximumDiagnosticQueue: 100, maximumFrameBytes: 128 * 1024 * 1024 });
    assert.equal(typeof transport.send, "function");
    assert.equal(typeof transport.setReceiveHandler, "function");
  } finally { close(); }
  assert.equal(installed(), undefined, "close removes the transport");
});

test("frames sent while the socket connects are sent once it opens, later ones at once", () => {
  const { socket, close } = install();
  try {
    installed()!.send({ neoastra: 1, kind: "hello" });
    assert.deepEqual(socket.sent, []);
    socket.open();
    assert.deepEqual(socket.sent, ['{"neoastra":1,"kind":"hello"}']);
    installed()!.send({ kind: "invoke", id: 1 });
    assert.deepEqual(socket.sent, ['{"neoastra":1,"kind":"hello"}', '{"kind":"invoke","id":1}']);
  } finally { close(); }
});

test("received JSON reaches the handler, and what came before a handler is kept for it in order", () => {
  const { socket, close } = install();
  try {
    socket.open();
    socket.receive('{"kind":"hello_ack"}');
    socket.receive("not json");
    socket.receive('{"kind":"event","n":2}');
    const received: unknown[] = [];
    const unregister = installed()!.setReceiveHandler(value => received.push(value));
    assert.deepEqual(received, [{ kind: "hello_ack" }, { kind: "event", n: 2 }]);
    socket.receive('{"kind":"event","n":3}');
    assert.deepEqual(received.at(-1), { kind: "event", n: 3 });
    unregister();
    socket.receive('{"kind":"event","n":4}');
    assert.equal(received.length, 3, "an unregistered handler gets nothing more");
    const later: unknown[] = [];
    installed()!.setReceiveHandler(value => later.push(value));
    assert.deepEqual(later, [{ kind: "event", n: 4 }], "what came in between waits for the next handler");
  } finally { close(); }
});

test("the socket closing is one close frame with the reason the page reloads on, and send throws afterwards", () => {
  const { socket, close } = install();
  try {
    socket.open();
    const received: unknown[] = [];
    installed()!.setReceiveHandler(value => received.push(value));
    socket.end();
    socket.end();
    assert.deepEqual(received, [closeFrame]);
    assert.throws(() => installed()!.send({ kind: "invoke" }), (error: unknown) =>
      typeof error === "object" && error !== null && (error as { code: unknown }).code === "connection_closed" && typeof (error as { message: unknown }).message === "string");
    close();
    assert.equal(received.length, 1, "close after the socket ended reports nothing more");
  } finally { close(); }
});

test("the close frame of a socket that closed before a handler exists waits for the handler", () => {
  const { socket, close } = install();
  try {
    socket.end();
    const received: unknown[] = [];
    installed()!.setReceiveHandler(value => received.push(value));
    assert.deepEqual(received, [closeFrame]);
  } finally { close(); }
});

test("closing the transport closes the socket and reports the close once", () => {
  const { socket, close } = install();
  socket.open();
  const received: unknown[] = [];
  installed()!.setReceiveHandler(value => received.push(value));
  close();
  assert.equal(socket.closeCalls, 1);
  assert.deepEqual(received, [closeFrame]);
  socket.end();
  assert.equal(received.length, 1);
});

test("a second install is refused and opens no socket while a transport is installed", () => {
  const first = install();
  try {
    let created = 0;
    const second = installRemoteTransport({ createSocket: () => { created++; return new FakeSocket(); }, url: "ws://127.0.0.1:2585/rpc" });
    assert.equal(second.installed, false);
    assert.equal(created, 0);
    second.close();
    assert.ok(installed(), "a refused install does not remove the installed transport");
  } finally { first.close(); }
});

test("the key is the one @neoastra/client discovers the transport under", () => {
  const client = readFileSync(new URL("../../node_modules/@neoastra/client/dist/index.js", import.meta.url), "utf8");
  assert.ok(client.includes('Symbol.for("@neoastra/client/transport/v1")'), "the client renamed its transport key: update remoteTransportKey");
  assert.equal(remoteTransportKey, Symbol.for("@neoastra/client/transport/v1"));
});
