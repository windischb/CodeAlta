import assert from "node:assert/strict";
import test from "node:test";
import { elementRect, popupPosition } from "./annotationPopupPlacement";

const place = (x: number, y: number, width: number, height: number) => ({ x, y, width, height, viewportWidth: 800, viewportHeight: 600 });

test("an element is drawn where its viewport fills the place of the page", () => {
  assert.deepEqual(elementRect(place(100, 200, 120, 30), { width: 800, height: 600 }), { left: 100, top: 200, width: 120, height: 30 });
  assert.deepEqual(elementRect(place(100, 200, 120, 30), { width: 400, height: 300 }), { left: 50, top: 100, width: 60, height: 15 });
  assert.equal(elementRect({ ...place(1, 2, 3, 4), viewportWidth: 0 }, { width: 400, height: 300 }), null);
});

test("the popup goes under the element, above it without room, and stays inside the place", () => {
  const area = { width: 800, height: 600 }, size = { width: 300, height: 160 };
  assert.deepEqual(popupPosition({ left: 100, top: 200, width: 120, height: 30 }, size, area), { left: 100, top: 238 });
  // Near the bottom: above the element.
  assert.deepEqual(popupPosition({ left: 100, top: 500, width: 120, height: 30 }, size, area), { left: 100, top: 332 });
  // Near the right edge, or out of the place at the left: moved inside.
  assert.deepEqual(popupPosition({ left: 700, top: 200, width: 80, height: 30 }, size, area), { left: 492, top: 238 });
  assert.deepEqual(popupPosition({ left: -40, top: 200, width: 80, height: 30 }, size, area), { left: 8, top: 238 });
  // An element as tall as the place: over its lower part, inside the place.
  assert.deepEqual(popupPosition({ left: 0, top: 0, width: 800, height: 600 }, size, area), { left: 8, top: 432 });
  // A place smaller than the popup: its top-left corner stays in sight.
  assert.deepEqual(popupPosition({ left: 50, top: 20, width: 10, height: 10 }, size, { width: 200, height: 100 }), { left: 8, top: 8 });
  // Without an element: centred across, in the upper third.
  assert.deepEqual(popupPosition(null, size, area), { left: 250, top: 146.66666666666666 });
});
