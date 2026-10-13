import assert from "node:assert/strict";
import test from "node:test";
import { createNativeViewOcclusion, crossed, intersects, modalSelector, overlayAttributes, overlaySelectors, type PageRect } from "./nativeViewOcclusion";

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
