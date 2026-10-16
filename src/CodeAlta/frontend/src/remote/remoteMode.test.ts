import assert from "node:assert/strict";
import test from "node:test";
import { isRemoteWindow, nextZoom, remoteDocumentHost, remoteHostName, remoteViewLabel, remoteWindowHost, zoomSteps } from "./remoteMode";

test("the mark of remote.ts on the document names the host before the connection exists", () => {
  assert.equal(remoteDocumentHost({ dataset: { codealtaRemote: "mac:2584" } as DOMStringMap }), "mac:2584");
  assert.equal(remoteDocumentHost({ dataset: {} as DOMStringMap }), null);
  assert.equal(remoteDocumentHost(null), null, "no document: not remote");
  assert.equal(remoteDocumentHost(), null, "no document in a test: not remote");
});

test("the host of a remote window is known from its connection; the window of the desktop app has none", () => {
  assert.equal(remoteWindowHost({ viewLabel: "remote" }, { host: "mac:2584" }), "mac:2584");
  assert.equal(remoteWindowHost({ viewLabel: "main" }, { host: "mac:2584" }), null);
  assert.equal(remoteWindowHost(undefined, { host: "mac:2584" }), null, "not connected yet: not remote");
});

test("a remote window zooms by the host's steps, and a zoom between steps moves to the step beyond", () => {
  assert.equal(zoomSteps[0], 25);
  assert.equal(zoomSteps[zoomSteps.length - 1], 500);
  assert.equal(nextZoom(100, 1), 110);
  assert.equal(nextZoom(100, -1), 90);
  assert.equal(nextZoom(105, 1), 110);
  assert.equal(nextZoom(105, -1), 100);
  assert.equal(nextZoom(500, 1), 500);
  assert.equal(nextZoom(25, -1), 25);
  assert.equal(nextZoom(175, 0), 100);
});

test("a window is remote when its view has the label of the remote server", () => {
  assert.equal(remoteViewLabel, "remote");
  assert.equal(isRemoteWindow({ viewLabel: "remote" }), true);
  assert.equal(isRemoteWindow({ viewLabel: "main" }), false);
  assert.equal(isRemoteWindow(undefined), false, "no runtime info yet is the window");
});

test("the remote host name is the host the page was loaded from, with its port", () => {
  assert.equal(remoteHostName({ host: "macbook.tail1234.ts.net:2584" }), "macbook.tail1234.ts.net:2584");
  assert.equal(remoteHostName({ host: "127.0.0.1:2585" }), "127.0.0.1:2585");
});
