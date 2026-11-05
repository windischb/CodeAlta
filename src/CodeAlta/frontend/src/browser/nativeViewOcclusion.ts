// What of the page has to appear above a native view. A native view lies above everything the page draws:
// a dialog, a menu or a toast that crosses it would be under it. The view is hidden while there is one.
//
// The application has no single component that every overlay goes through, so the document is watched for
// the elements overlays are made of. The `title` tooltips of the system cannot be seen from the page: they
// are left to appear under the view.
//
// A toast is the one overlay that stays without the user asking for it (the notice of an update stays until
// it is closed), so toasts are kept out of the way instead: the toasters of the window are moved beside the
// parts of the window that show a native view. A toast that crosses a view all the same hides it, as before.

/** A rectangle of the page, in CSS pixels. */
export type PageRect = Readonly<{ left: number; top: number; right: number; bottom: number }>;

/** A modal dialog: it takes the whole window, wherever its box is. */
export const modalSelector = 'dialog[open], [role="dialog"][aria-modal="true"]';

/**
 * The elements that are drawn above what is under them, each with what it is. A native view is hidden while
 * the box of one of them crosses it. Class names of Blueprint are the ones of `@blueprintjs/core` 6
 * (`common/classes.js`): they are in the document only while their overlay is open or closing.
 */
export const overlaySelectors: readonly string[] = Object.freeze([
  // The backdrop of a Blueprint dialog, alert or context menu: it covers the window.
  ".bp6-overlay-backdrop",
  // What an open Blueprint overlay shows: the box of a dialog, the container of a popover.
  ".bp6-overlay-open > .bp6-overlay-content",
  // A popover and what it holds: menus, pickers, confirmations.
  ".bp6-popover",
  // A notification of the application or of an update.
  ".bp6-toast",
  // The menu of a session tab or of a tab strip.
  ".session-tab-popup",
  // The banners of the window: a host that is silent, a width that could not be saved.
  ".shell-notices",
  // The guided tour, which covers the window while it runs.
  ".guided-tour",
  // Where a session tab that is dragged would be docked.
  ".session-drop-preview",
  // The menu of the composer's actions, a `details` element that opens over the timeline.
  ".composer-actions-menu[open] > div",
  // The menu of a session row of the Explorer, which hangs below its row.
  ".session-actions-menu",
]);

/** The attributes that say an overlay opened or closed without an element being added or removed. */
export const overlayAttributes: readonly string[] = Object.freeze(["open", "class", "role", "aria-modal", "hidden"]);

/** Whether two rectangles share some area. One without area shares none. */
export function intersects(a: PageRect, b: PageRect): boolean {
  return a.right > a.left && a.bottom > a.top && b.right > b.left && b.bottom > b.top
    && a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;
}

/** Whether one of the boxes crosses a rectangle. */
export function crossed(rect: PageRect, boxes: Iterable<PageRect>): boolean {
  for (const box of boxes) if (intersects(rect, box)) return true;
  return false;
}

/** The least width toasts are given beside native views, in CSS pixels: a toast of 300 and the margins of its container. */
export const leastToastWidth = 340;
/** The properties of the root element that say where the toasters are: how far from the left and the right edge of the window. */
export const toastInsetProperties = Object.freeze({ left: "--native-view-toast-left", right: "--native-view-toast-right" });

/**
 * Where toasts go in a window of `width` so that they cross none of `parts`, the parts of the window that show
 * a native view: the rightmost stretch of the width that no part takes and that is at least `least` wide, as
 * the distances from the left and the right edge of the window. Null when no part takes anything, or no
 * stretch is wide enough: the toasters are then where they are by themselves.
 */
export function toastInsets(width: number, parts: Iterable<PageRect>, least = leastToastWidth): Readonly<{ left: number; right: number }> | null {
  const taken = [...parts].map(part => ({ left: Math.max(0, part.left), right: Math.min(width, part.right) })).filter(part => part.right > part.left).sort((a, b) => a.left - b.left);
  if (!(width > 0) || taken.length === 0) return null;
  // The stretches between the parts, from the left; the last one that is wide enough is the one.
  let free: { left: number; right: number } | null = null, edge = 0;
  for (const part of [...taken, { left: width, right: width }]) {
    if (part.left - edge >= least) free = { left: edge, right: part.left };
    edge = Math.max(edge, part.right);
  }
  return free && { left: Math.round(free.left), right: Math.round(width - free.right) };
}

export type NativeViewOcclusion = ReturnType<typeof createNativeViewOcclusion>;

/**
 * Watches a document for what has to appear above a native view. The document is watched only while someone
 * listens. It is searched again when an element was added or removed, or one of `overlayAttributes` changed,
 * since the last search: a page with long timelines holds tens of thousands of elements. The boxes of what
 * was found are measured at each question, since an overlay moves without the document changing.
 */
export function createNativeViewOcclusion(root: Document = document, frames: Readonly<{ request: (run: () => void) => number; cancel: (frame: number) => void }> = {
  request: run => requestAnimationFrame(run), cancel: frame => cancelAnimationFrame(frame) }) {
  const listeners = new Set<() => void>();
  let changes: MutationObserver | null = null;
  let frame: number | null = null;
  // What the last search found; null when the document changed since.
  let found: { modal: boolean; overlays: readonly Element[] } | null = null;
  // The parts of the window that show a native view, by who shows it, and where the toasters were last put.
  const parts = new Map<object, PageRect>();
  let insets: Readonly<{ left: number; right: number }> | null = null;
  // The width of the window when the toasters were last put.
  let width = 0;

  function placeToasts() {
    const next = toastInsets(root.documentElement.clientWidth, parts.values());
    if (next?.left === insets?.left && next?.right === insets?.right) return;
    insets = next;
    const style = root.documentElement.style;
    if (next) { style.setProperty(toastInsetProperties.left, `${next.left}px`); style.setProperty(toastInsetProperties.right, `${next.right}px`); }
    else { style.removeProperty(toastInsetProperties.left); style.removeProperty(toastInsetProperties.right); }
  }

  function changed() {
    found = null;
    // Many changes of one frame are one check.
    frame ??= frames.request(() => { frame = null; for (const listener of [...listeners]) listener(); });
  }

  function search() {
    // Changes made in this very task have not reached the observer yet: they are taken here.
    if (changes && changes.takeRecords().length > 0) found = null;
    // Without an observer nothing says when to look again: every question searches.
    if (!found || !changes) found = { modal: !!root.querySelector(modalSelector), overlays: Array.from(root.querySelectorAll(overlaySelectors.join(", "))) };
    return found;
  }

  return {
    /**
     * Calls `listener` when what covers the page may have changed: at most once for each frame. Returns what
     * stops it.
     */
    subscribe(listener: () => void) {
      listeners.add(listener);
      if (!changes && typeof MutationObserver !== "undefined") {
        changes = new MutationObserver(changed);
        changes.observe(root.body, { subtree: true, childList: true, attributes: true, attributeFilter: [...overlayAttributes] });
        found = null;
      }
      return () => {
        if (!listeners.delete(listener) || listeners.size > 0) return;
        changes?.disconnect();
        changes = null;
        found = null;
        if (frame !== null) { frames.cancel(frame); frame = null; }
      };
    },
    /**
     * Says which part of the window `owner` shows a native view in, with its frame, or with null that it shows
     * none any more: the toasters of the window are put beside these parts (`toastInsets`), through two
     * properties of the root element that their stylesheet reads. It is asked at each frame: nothing is done
     * while nothing moved.
     */
    reserve(owner: object, rect: PageRect | null) {
      const before = parts.get(owner), wide = root.documentElement.clientWidth;
      if (!rect) { if (!parts.delete(owner)) return; }
      else if (before && before.left === rect.left && before.right === rect.right && wide === width) return;
      else parts.set(owner, { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom });
      width = wide;
      placeToasts();
    },
    /**
     * Whether something of the page has to appear above a native view shown in `rect`: a modal dialog is
     * open, or the box of an overlay crosses the rectangle. `within` is the element that holds the view: an
     * overlay inside it is its own, drawn beside the view, and does not count.
     */
    occludes(rect: PageRect, within: Element | null = null): boolean {
      const { modal, overlays } = search();
      if (modal) return true;
      for (const overlay of overlays) {
        if (within?.contains(overlay)) continue;
        if (intersects(rect, overlay.getBoundingClientRect())) return true;
      }
      return false;
    },
  };
}
