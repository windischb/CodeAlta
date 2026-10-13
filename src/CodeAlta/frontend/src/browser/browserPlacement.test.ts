import assert from "node:assert/strict";
import test from "node:test";
import { locales, translate, type MessageKey } from "../localization";
import { browserMessages } from "./messages";
import { browserCoversPane, browserPaneOf, browserPanesKey, defaultBrowserPane, deviceBounds, maximumBrowserPanes, maximumBrowserWidth, minimumBrowserWidth,
  minimumConversationWidth, parseBrowserPanes, persistBrowserPanes, resizeBrowserWidth, sameBounds, withBrowserPane, withoutBrowserPanes } from "./browserPlacement";

test("a placeholder is placed in the pixels of the display, with its edges rounded", () => {
  assert.deepEqual(deviceBounds({ left: 10, top: 20, width: 300, height: 200 }, 1), { x: 10, y: 20, width: 300, height: 200 });
  assert.deepEqual(deviceBounds({ left: 10, top: 20, width: 300, height: 200 }, 1.5), { x: 15, y: 30, width: 450, height: 300 });
  // 100.5 * 1.25 = 125.625 and 300.75 * 1.25 = 375.9375: the edges are 126 and 376, not 126 + round(250.3).
  assert.deepEqual(deviceBounds({ left: 100.5, top: 0, width: 200.25, height: 10 }, 1.25), { x: 126, y: 0, width: 250, height: 13 });
  // Two neighbours that share an edge share it on the display, whatever the scale.
  for (const scale of [1, 1.25, 1.5, 1.75, 2.25]) {
    for (const edge of [333.3, 401.5, 512.7]) {
      const left = deviceBounds({ left: 7.3, top: 0, width: edge - 7.3, height: 50 }, scale)!;
      const right = deviceBounds({ left: edge, top: 0, width: 240.4, height: 50 }, scale)!;
      assert.equal(left.x + left.width, right.x, `${scale} ${edge}`);
    }
  }
});

test("a rectangle without area, or one that cannot be measured, shows no view", () => {
  assert.equal(deviceBounds({ left: 10, top: 20, width: 0, height: 200 }, 1), null);
  assert.equal(deviceBounds({ left: 10, top: 20, width: 300, height: 0 }, 2), null);
  assert.equal(deviceBounds({ left: 10, top: 20, width: 0.2, height: 200 }, 1), null, "less than a pixel of the display");
  assert.equal(deviceBounds({ left: 10, top: 20, width: -5, height: 200 }, 1), null);
  assert.equal(deviceBounds({ left: Number.NaN, top: 20, width: 300, height: 200 }, 1), null);
  assert.equal(deviceBounds({ left: 10, top: 20, width: 300, height: 200 }, 0), null);
});

test("two placements are the same one when their rectangles are, or when both are none", () => {
  const bounds = { x: 1, y: 2, width: 3, height: 4 };
  assert.equal(sameBounds(bounds, { ...bounds }), true);
  assert.equal(sameBounds(null, null), true);
  assert.equal(sameBounds(bounds, null), false);
  assert.equal(sameBounds(null, bounds), false);
  for (const change of [{ x: 0 }, { y: 0 }, { width: 0 }, { height: 0 }]) assert.equal(sameBounds(bounds, { ...bounds, ...change }), false);
});

test("the browser part of a session is closed with the default width until it is changed", () => {
  assert.equal(browserPanesKey, "codealta.desktop.browser.v1");
  const none = parseBrowserPanes(null);
  assert.equal(browserPaneOf(none, "S1"), defaultBrowserPane);
  const opened = withBrowserPane(none, "S1", { open: true });
  // Sessions are known by their lower-case id.
  assert.deepEqual([...opened], [["s1", { open: true, width: defaultBrowserPane.width }]]);
  assert.deepEqual(browserPaneOf(opened, "s1"), browserPaneOf(opened, "S1"));
  assert.equal(withBrowserPane(opened, "s1", { open: true }), opened, "nothing changed");
  const narrow = withBrowserPane(opened, "s1", { width: 10 });
  assert.equal(browserPaneOf(narrow, "s1").width, minimumBrowserWidth);
  assert.equal(browserPaneOf(withBrowserPane(opened, "s1", { width: 1e9 }), "s1").width, maximumBrowserWidth);
  // Closed with a width of its own, the width is kept; closed with the default one, nothing is.
  assert.deepEqual([...withBrowserPane(narrow, "s1", { open: false })], [["s1", { open: false, width: minimumBrowserWidth }]]);
  assert.equal(withBrowserPane(opened, "s1", { open: false }).size, 0);
  assert.equal(withBrowserPane(none, "", { open: true }), none);
});

test("the splitter is on the left of the browser part: a move to the left widens it", () => {
  assert.equal(resizeBrowserWidth(500, -40), 540);
  assert.equal(resizeBrowserWidth(500, 40), 460);
  assert.equal(resizeBrowserWidth(minimumBrowserWidth, 40), minimumBrowserWidth);
  assert.equal(resizeBrowserWidth(maximumBrowserWidth, -40), maximumBrowserWidth);
});

test("what was kept is read back, without what cannot be read", () => {
  let saved = "";
  const panes = withBrowserPane(withBrowserPane(parseBrowserPanes(null), "a", { open: true, width: 400 }), "b", { width: 700 });
  assert.equal(persistBrowserPanes(value => { saved = value; }, panes), true);
  assert.deepEqual(JSON.parse(saved), { a: { open: true, width: 400 }, b: { open: false, width: 700 } });
  assert.deepEqual([...parseBrowserPanes(saved)], [...panes]);
  assert.equal(persistBrowserPanes(() => { throw Error("full"); }, panes), false);
  for (const malformed of ["", "{", "null", "[]", "3", '"text"']) assert.equal(parseBrowserPanes(malformed).size, 0, malformed);
  const mixed = parseBrowserPanes(JSON.stringify({ A: { open: true, width: 5 }, b: { open: "yes", width: 400 }, c: { open: true }, d: null, e: { open: false, width: 1e999 }, "": { open: true, width: 400 } }));
  assert.deepEqual([...mixed], [["a", { open: true, width: minimumBrowserWidth }]]);
});

test("the sessions changed longest ago are forgotten beyond the limit, and the sessions that are gone when asked", () => {
  let panes = parseBrowserPanes(null);
  for (let index = 0; index < maximumBrowserPanes + 3; index++) panes = withBrowserPane(panes, `s${index}`, { open: true });
  assert.equal(panes.size, maximumBrowserPanes);
  assert.equal(panes.has("s0"), false);
  assert.equal(panes.has("s3"), true);
  // A session that is changed again is the one changed last.
  panes = withBrowserPane(panes, "s3", { width: 444 });
  panes = withBrowserPane(panes, "new", { open: true });
  assert.equal(panes.has("s3"), true);
  assert.equal(panes.has("s4"), false);
  assert.equal(parseBrowserPanes(JSON.stringify(Object.fromEntries([...panes, ["x1", { open: true, width: 400 }], ["x2", { open: true, width: 400 }]]))).size, maximumBrowserPanes);
  const kept = withoutBrowserPanes(panes, id => id === "s3" || id === "new");
  assert.deepEqual([...kept.keys()], ["s3", "new"]);
  assert.equal(withoutBrowserPanes(kept, () => true), kept);
});

test("a pane too narrow for a conversation beside a browser gives the browser the whole pane", () => {
  assert.equal(browserCoversPane(minimumConversationWidth + minimumBrowserWidth), false);
  assert.equal(browserCoversPane(minimumConversationWidth + minimumBrowserWidth - 1), true);
  assert.equal(browserCoversPane(1600), false);
  assert.equal(browserCoversPane(0), false, "a pane that is not laid out says nothing");
});

test("Browser messages have complete localized placeholders in every shell language", () => {
  // The word German has for it.
  const sameWord = new Set(["de: Browser", "fr: Annotations"]);
  for (const key of Object.keys(browserMessages) as MessageKey[]) {
    const placeholders = (key.match(/\{\w+\}/g) ?? []).sort();
    for (const locale of locales) {
      const translated = translate(locale, key);
      assert.ok(translated.length > 0);
      assert.deepEqual((translated.match(/\{\w+\}/g) ?? []).sort(), placeholders, `${locale}: ${key}`);
      if (locale !== "en" && !sameWord.has(`${locale}: ${key}`)) assert.notEqual(translated, key, `${locale}: ${key}`);
    }
  }
});
