import assert from "node:assert/strict";
import test from "node:test";
import { createElement, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { ShellLanguageContext } from "../shellLanguage";
import { bindChoices, bindLabel, bindWarning, type RemoteHostState } from "./remoteHost";
import { RemoteHostSettings, RemoteHostView } from "./RemoteHostSettings";

const never = () => assert.fail("rendering must not act");
const render = (element: ReactElement) =>
  renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } }, element));
// The host is asked nothing while a view is drawn.
const silent = () => new Promise<never>(() => { });
const addresses = [{ host: "127.0.0.1", name: "" }, { host: "100.64.0.5", name: "Tailscale" }, { host: "192.168.1.20", name: "Ethernet" }, { host: "0.0.0.0", name: "" }];
const state = (change: Partial<RemoteHostState> = {}): RemoteHostState => ({ status: "ok", state: "running", enabled: true, url: "http://127.0.0.1:2584/",
  error: null, clients: 2, bind: "127.0.0.1", addresses, ...change });
const t = (key: string) => key;

test("the choices of address are named by their network, and any address but this computer's is warned about", () => {
  assert.equal(bindLabel({ host: "127.0.0.1", name: "" }, t), "This computer only");
  assert.equal(bindLabel({ host: "localhost", name: "" }, t), "This computer only");
  assert.equal(bindLabel({ host: "100.64.0.5", name: "Tailscale" }, t), "Tailscale (100.64.0.5)");
  assert.equal(bindLabel({ host: "10.0.0.2", name: "" }, t), "10.0.0.2");
  assert.equal(bindLabel({ host: "0.0.0.0", name: "" }, t), "All networks");
  assert.equal(bindWarning("127.0.0.1"), false);
  assert.equal(bindWarning("localhost"), false);
  assert.equal(bindWarning("100.64.0.5"), true);
  assert.equal(bindWarning("0.0.0.0"), true);
  // An address of the command line that the host does not list is still a choice, the current one.
  assert.equal(bindChoices(addresses, "100.64.0.5").length, 4);
  assert.deepEqual(bindChoices(addresses, "::1").at(-1), { host: "::1", name: "" });
});

test("the page is drawn before the host answered, and says what it is for", () => {
  const html = render(createElement(RemoteHostSettings, { epoch: "e", api: { status: silent, setEnabled: never, setBind: never } }));
  assert.match(html, /<h1>Remote access<\/h1>/);
  assert.match(html, /The page of CodeAlta in a browser on another computer, with everything this window does\./);
  assert.match(html, /Loading…/);
});

test("a running server shows the warning, where it listens, its address, a QR code and the connected windows", () => {
  const html = render(createElement(RemoteHostView, { state: state(), busy: false, onEnable: never, onBind: never, copy: never }));
  assert.match(html, /<input[^>]*type="checkbox"[^>]*checked=""/);
  assert.match(html, /Allow other computers to use this CodeAlta/);
  assert.match(html, /There is no login yet/);
  assert.match(html, /<option[^>]*value="127\.0\.0\.1"[^>]*selected=""[^>]*>This computer only<\/option>/);
  assert.match(html, /<option value="100\.64\.0\.5">Tailscale \(100\.64\.0\.5\)<\/option>/);
  assert.match(html, /<option value="0\.0\.0\.0">All networks<\/option>/);
  assert.equal(html.includes("Other computers on that network can reach this CodeAlta."), false);
  assert.match(html, /value="http:\/\/127\.0\.0\.1:2584\/"/);
  assert.match(html, /data-remote-control-qr/);
  assert.match(html, /Connected windows: 2/);

  const open = render(createElement(RemoteHostView, { state: state({ bind: "100.64.0.5", url: "http://100.64.0.5:2584/" }), busy: false, onEnable: never, onBind: never, copy: never }));
  assert.match(open, /<option[^>]*value="100\.64\.0\.5"[^>]*selected=""/);
  assert.match(open, /Other computers on that network can reach this CodeAlta\./);
});

test("a server that is off, or that could not listen, shows nothing to open", () => {
  const off = render(createElement(RemoteHostView, { state: state({ state: "stopped", enabled: false, url: null, clients: 0 }), busy: false, onEnable: never, onBind: never, copy: never }));
  assert.equal(/<input[^>]*type="checkbox"[^>]*checked=""/.test(off), false);
  assert.equal(off.includes("There is no login yet"), false, "the warning goes with the switch");
  assert.match(off, /Listen on/, "the address is chosen before the server is turned on");
  assert.equal(off.includes("data-remote-control-qr"), false);
  assert.equal(off.includes("Connected windows"), false);

  const failed = render(createElement(RemoteHostView, { state: state({ state: "failed", url: null, clients: 0, error: "Only one usage of each socket address is normally permitted." }),
    busy: false, onEnable: never, onBind: never, copy: never }));
  assert.match(failed, /The remote server is not listening\./);
  assert.match(failed, /Only one usage of each socket address is normally permitted\./);
  assert.equal(failed.includes("data-remote-control-qr"), false);
  // The switch stays on: the server is turned on, and says why it does not run.
  assert.match(failed, /<input[^>]*type="checkbox"[^>]*checked=""/);
});
