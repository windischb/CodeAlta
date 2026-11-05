import assert from "node:assert/strict";
import test from "node:test";
import type { BrowserPlaceRequest, BrowserReply } from "#neoastra";
import { boxOf, createBrowserHub, sameBox, type BrowserApi } from "./browserHub";

const ok: BrowserReply = { status: "ok", session: null, pageId: null };

// A host that answers at once and records the places it is told: it has the built-in browser.
function fakeHost() {
  const places: BrowserPlaceRequest[] = [];
  const api = {
    state: async (request: { sessionId: string }) => (request.sessionId ? ok : { ...ok, status: "invalid" }),
    open: async () => ok, close: async () => ok, select: async () => ok, move: async () => ok,
    navigate: async () => ok, command: async () => ok, capture: async () => ({ status: "ok", image: "data:image/jpeg;base64,", width: 1, height: 1 }),
    place: async (request: BrowserPlaceRequest) => { places.push(request); return ok; },
  } as unknown as BrowserApi;
  return { api, places };
}

const tick = () => new Promise(resolve => setTimeout(resolve, 0));

test("a place is a rectangle with an area, and two places are the same to a quarter of a pixel", () => {
  assert.deepEqual(boxOf({ left: 10, top: 20, width: 300, height: 200 }), { x: 10, y: 20, width: 300, height: 200 });
  assert.equal(boxOf({ left: 10, top: 20, width: 0, height: 200 }), null);
  assert.equal(boxOf({ left: Number.NaN, top: 20, width: 10, height: 200 }), null);
  assert.equal(sameBox({ x: 1, y: 2, width: 3, height: 4 }, { x: 1.1, y: 2, width: 3, height: 4 }), true);
  assert.equal(sameBox({ x: 1, y: 2, width: 3, height: 4 }, { x: 1.5, y: 2, width: 3, height: 4 }), false);
  assert.equal(sameBox(null, null), true);
  assert.equal(sameBox(null, { x: 1, y: 2, width: 3, height: 4 }), false);
});

test("the host is told where this window shows a browser once for each change, and that it no longer shows it when the page stops listening", async () => {
  const host = fakeHost();
  const hub = createBrowserHub(host.api, { set: () => 0, clear: () => { } });
  const disconnect = hub.connect("epoch-1");
  await tick();
  assert.equal(hub.supported(), true);

  hub.place("s1", { x: 10, y: 20, width: 300, height: 200 });
  hub.place("s1", { x: 10, y: 20, width: 300, height: 200 });
  await tick();
  hub.place("s1", { x: 10.1, y: 20, width: 300, height: 200 });
  await tick();
  assert.deepEqual(host.places.map(place => [place.sessionId, place.visible, place.x]), [["s1", true, 10]], "The same place is told once.");

  hub.place("s1", { x: 40, y: 20, width: 300, height: 200 });
  await tick();
  hub.place("s1", null);
  await tick();
  assert.deepEqual(host.places.map(place => [place.visible, place.x]), [[true, 10], [true, 40], [false, 0]]);

  hub.place("s2", { x: 0, y: 0, width: 100, height: 100 });
  await tick();
  disconnect();
  await tick();
  assert.deepEqual(host.places.slice(-2).map(place => [place.sessionId, place.visible]), [["s2", true], ["s2", false]], "No view stays where nothing draws its frame.");
});
