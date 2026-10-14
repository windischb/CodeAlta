// Where the popup of an annotation goes in the place of the page: at the element, over a still picture of the page.
import type { BrowserElementPlaceItem } from "#neoastra";

/** A size in CSS pixels of the window. */
export type PopupSize = Readonly<{ width: number; height: number }>;
/** A rectangle in CSS pixels of the window, from the top-left corner of the place of the page. */
export type PopupRect = Readonly<{ left: number; top: number; width: number; height: number }>;

/** The space kept between the popup and the element, and between the popup and the edges of the place. */
const gap = 8;

/**
 * Where an element is drawn in the place of the page: the page reports it in CSS pixels of its viewport, which
 * fills the place. Null for a place without a viewport.
 */
export function elementRect(place: BrowserElementPlaceItem, area: PopupSize): PopupRect | null {
  if (place.viewportWidth <= 0 || place.viewportHeight <= 0) return null;
  const x = area.width / place.viewportWidth, y = area.height / place.viewportHeight;
  return { left: place.x * x, top: place.y * y, width: place.width * x, height: place.height * y };
}

const clamp = (value: number, low: number, high: number) => Math.max(low, Math.min(value, Math.max(low, high)));

/**
 * Where the popup goes: under the element, above it when there is no room under it, and otherwise over its lower
 * part; at the left edge of the element. It stays inside the place, and is centred without an element.
 */
export function popupPosition(target: PopupRect | null, size: PopupSize, area: PopupSize): Readonly<{ left: number; top: number }> {
  if (!target) return { left: Math.max(gap, (area.width - size.width) / 2), top: Math.max(gap, (area.height - size.height) / 3) };
  const below = target.top + target.height + gap, above = target.top - gap - size.height;
  const top = below + size.height <= area.height - gap ? below
    : above >= gap ? above
    : clamp(below, gap, area.height - size.height - gap);
  return { left: clamp(target.left, gap, area.width - size.width - gap), top };
}
