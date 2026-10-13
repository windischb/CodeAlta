// What of the page has to appear above a native view. A native view lies above everything the page draws:
// a dialog, a menu or a toast that crosses it would be under it. The view is hidden while there is one.
//
// The application has no single component that every overlay goes through, so the document is watched for
// the elements overlays are made of. The `title` tooltips of the system cannot be seen from the page: they
// are left to appear under the view.

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
