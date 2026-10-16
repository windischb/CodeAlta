import assert from "node:assert/strict";
import test from "node:test";
import { isRemoteWindow, remoteHostName, remoteViewLabel } from "./remoteMode";

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
