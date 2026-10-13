import assert from "node:assert/strict";
import test from "node:test";
import { createNativeViewOcclusion, crossed, intersects, leastToastWidth, modalSelector, overlayAttributes, overlaySelectors, toastInsetProperties, toastInsets, type PageRect } from "./nativeViewOcclusion";

const rect = (left: number, top: number, width: number, height: number): PageRect => ({ left, top, right: left + width, bottom: top + height });

test("two rectangles cross when they share some area", () => {
  const view = rect(500, 100, 400, 300);
  assert.equal(intersects(view, rect(600, 200, 50, 50)), true, "inside");
  assert.equal(intersects(view, rect(0, 0, 2000, 2000)), true, "around");
  assert.equal(intersects(view, rect(450, 50, 100, 100)), true, "over a corner");
  assert.equal(intersects(rect(600, 200, 50, 50), view), true, "either way");
  // Neighbours that share an edge share no area.
  assert.equal(intersects(view, rect(100, 100, 400, 300)), false, "at its left");
  assert.equal(intersects(view, rect(900, 100, 50, 300)), false, "at its right");
  assert.equal(intersects(view, rect(500, 0, 400, 100)), false, "above");
  assert.equal(intersects(view, rect(500, 400, 400, 50)), false, "below");
  assert.equal(intersects(view, rect(0, 0, 100, 100)), false, "elsewhere");
  // What has no area covers nothing: an overlay that is not laid out, an empty container of notices.
  assert.equal(intersects(view, rect(600, 200, 0, 50)), false);
  assert.equal(intersects(view, rect(600, 200, 50, 0)), false);
  assert.equal(intersects(rect(600, 200, 0, 0), rect(0, 0, 2000, 2000)), false);
});

test("a view is crossed when one of the boxes crosses it", () => {
  const view = rect(500, 100, 400, 300);
  assert.equal(crossed(view, []), false);
  assert.equal(crossed(view, [rect(0, 0, 100, 100), rect(0, 500, 100, 100)]), false);
  assert.equal(crossed(view, [rect(0, 0, 100, 100), rect(880, 380, 100, 100)]), true);
});

test("toasts go to the rightmost stretch of the window that no native view takes", () => {
  // No browser is shown: the toasters are where they are by themselves.
  assert.equal(toastInsets(1600, []), null);
  assert.equal(toastInsets(0, [rect(0, 0, 10, 10)]), null);
  // One session with its browser at the right: beside it, over the conversation.
  assert.deepEqual(toastInsets(1600, [rect(1040, 80, 560, 800)]), { left: 0, right: 560 });
  // Two sessions side by side, each with its browser: the conversation of the right one.
  assert.deepEqual(toastInsets(2000, [rect(500, 80, 500, 800), rect(1500, 80, 500, 800)]), { left: 1000, right: 500 });
  // The right conversation is too narrow for a toast: the left one.
  assert.deepEqual(toastInsets(1600, [rect(500, 80, 500, 800), rect(1200, 80, 400, 800)]), { left: 0, right: 1100 });
  // A browser that is not at the right edge leaves the right of the window free.
  assert.deepEqual(toastInsets(1600, [rect(300, 80, 500, 800)]), { left: 800, right: 0 });
  // The stretch has the least width, and not a pixel less.
  assert.deepEqual(toastInsets(1000, [rect(leastToastWidth, 0, 1000, 800)]), { left: 0, right: 1000 - leastToastWidth });
  assert.equal(toastInsets(1000, [rect(leastToastWidth - 1, 0, 1000, 800)]), null);
  // A narrow window whose browser takes it all, parts that overlap, and parts without width.
  assert.equal(toastInsets(700, [rect(0, 80, 700, 600)]), null);
  assert.deepEqual(toastInsets(1600, [rect(900, 0, 400, 800), rect(1000, 0, 600, 800)]), { left: 0, right: 700 });
  assert.equal(toastInsets(1600, [rect(900, 0, 0, 800)]), null);
});

test("the toasters are moved beside the parts that show a native view, and back when none does", () => {
  const properties = new Map<string, string>();
  let writes = 0;
  const root = { body: {}, documentElement: { clientWidth: 1600, style: {
    setProperty: (name: string, value: string) => { properties.set(name, value); writes++; },
    removeProperty: (name: string) => { properties.delete(name); writes++; } } } } as unknown as Document;
  const occlusion = createNativeViewOcclusion(root, { request: () => 0, cancel: () => { } });
  const first = {}, second = {};
  occlusion.reserve(first, rect(1040, 80, 560, 800));
  assert.deepEqual(Object.fromEntries(properties), { [toastInsetProperties.left]: "0px", [toastInsetProperties.right]: "560px" });
  // Asked again at each frame: nothing is written while nothing moved.
  const written = writes;
  occlusion.reserve(first, rect(1040, 90, 560, 700));
  assert.equal(writes, written);
  // The splitter moved; then another session shows its browser beside this one.
  occlusion.reserve(first, rect(940, 80, 660, 800));
  assert.equal(properties.get(toastInsetProperties.right), "660px");
  occlusion.reserve(second, rect(0, 80, 400, 800));
  assert.deepEqual(Object.fromEntries(properties), { [toastInsetProperties.left]: "400px", [toastInsetProperties.right]: "660px" });
  // The window got narrower without the parts moving: the room is looked at again.
  (root.documentElement as unknown as { clientWidth: number }).clientWidth = 1200;
  occlusion.reserve(second, rect(0, 80, 400, 800));
  assert.deepEqual(Object.fromEntries(properties), { [toastInsetProperties.left]: "400px", [toastInsetProperties.right]: "260px" });
  occlusion.reserve(second, null);
  occlusion.reserve(first, null);
  assert.equal(properties.size, 0);
  const cleared = writes;
  occlusion.reserve(first, null);
  assert.equal(writes, cleared);
});

test("the overlays of the window are named once, and each name is a selector", () => {
  assert.ok(overlaySelectors.length >= 8);
  assert.equal(new Set(overlaySelectors).size, overlaySelectors.length);
  for (const selector of overlaySelectors) assert.match(selector, /^[.\[][\w\-.\[\]="> ]+$/, selector);
  // The overlays of Blueprint, by the class names of its version 6, and the ones of the application.
  for (const expected of [".bp6-popover", ".bp6-toast", ".bp6-overlay-backdrop", ".session-tab-popup", ".shell-notices", ".guided-tour", ".session-drop-preview"])
    assert.ok(overlaySelectors.includes(expected), expected);
  assert.ok(modalSelector.includes("dialog[open]") && modalSelector.includes('[aria-modal="true"]'));
  for (const attribute of ["open", "class", "aria-modal"]) assert.ok(overlayAttributes.includes(attribute), attribute);
});

test("a native view is covered by a modal dialog anywhere, and by an overlay that crosses it", () => {
  // A document that holds what a test puts in it. Without an observer every question searches it again.
  const held = { modal: false, overlays: [] as { box: PageRect }[], searches: 0 };
  const element = (box: PageRect) => ({ box, getBoundingClientRect: () => box });
  const root = {
    body: {},
    querySelector: (selector: string) => { assert.equal(selector, modalSelector); return held.modal ? {} : null; },
    querySelectorAll: (selector: string) => { assert.equal(selector, overlaySelectors.join(", ")); held.searches++; return held.overlays; },
  } as unknown as Document;
  const occlusion = createNativeViewOcclusion(root, { request: () => 0, cancel: () => { } });
  const view = rect(500, 100, 400, 300);
  assert.equal(occlusion.occludes(view), false);
  const menu = element(rect(100, 100, 200, 300));
  held.overlays = [menu];
  assert.equal(occlusion.occludes(view), false, "a menu beside the view");
  held.overlays = [menu, element(rect(450, 150, 200, 100))];
  assert.equal(occlusion.occludes(view), true, "a menu over its edge");
  // An overlay of the frame of the view itself is drawn beside the view.
  const own = held.overlays[1];
  const frame = { contains: (candidate: unknown) => candidate === own } as unknown as Element;
  assert.equal(occlusion.occludes(view, frame), false);
  held.overlays = [];
  held.modal = true;
  assert.equal(occlusion.occludes(view), true, "a modal dialog covers the window");
  assert.equal(occlusion.occludes(rect(0, 0, 1, 1)), true);
  assert.ok(held.searches >= 4);
});
