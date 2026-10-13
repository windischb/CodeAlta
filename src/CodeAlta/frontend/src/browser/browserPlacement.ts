// Where the browser of a session is: the rectangle the host places its view in, and what the window keeps of
// the browser part of each session (whether it is open, and how wide).

/** A rectangle of the page of the application, in the pixels of the display, from its top-left corner. */
export type BrowserBounds = Readonly<{ x: number; y: number; width: number; height: number }>;
/** What a placeholder measures: `getBoundingClientRect()`, in CSS pixels. */
export type CssRect = Readonly<{ left: number; top: number; width: number; height: number }>;

/**
 * The rectangle of the display a placeholder covers. Its edges are rounded, not its size: two neighbours that
 * share an edge in CSS pixels share it in device pixels too, and never overlap by one. `scale` is
 * `window.devicePixelRatio`, which already includes the zoom of the window's view. Null for a rectangle
 * without area (a pane that is not laid out), which shows no view.
 */
export function deviceBounds(rect: CssRect, scale: number): BrowserBounds | null {
  if (![rect.left, rect.top, rect.width, rect.height, scale].every(Number.isFinite) || !(scale > 0)) return null;
  const left = Math.round(rect.left * scale), top = Math.round(rect.top * scale);
  const right = Math.round((rect.left + rect.width) * scale), bottom = Math.round((rect.top + rect.height) * scale);
  return right > left && bottom > top ? { x: left, y: top, width: right - left, height: bottom - top } : null;
}

/** Whether two placements are the same one: the same rectangle, or none on both sides. */
export function sameBounds(a: BrowserBounds | null, b: BrowserBounds | null): boolean {
  return a === b || (!!a && !!b && a.x === b.x && a.y === b.y && a.width === b.width && a.height === b.height);
}

/** The browser part of one session: whether it is open, and its width in CSS pixels. */
export type BrowserPaneState = Readonly<{ open: boolean; width: number }>;
/** The browser parts the window remembers, by the lower-case id of their session; the one changed last is last. */
export type BrowserPanes = ReadonlyMap<string, BrowserPaneState>;

export const browserPanesKey = "codealta.desktop.browser.v1";
/** Width bounds in CSS pixels; the splitter's ARIA range uses the same values. The pane itself limits what fits. */
export const minimumBrowserWidth = 320;
export const maximumBrowserWidth = 2400;
export const defaultBrowserPane: BrowserPaneState = Object.freeze({ open: false, width: 560 });
/** The least width a conversation keeps beside its browser. */
export const minimumConversationWidth = 360;
/** How many sessions are remembered: beyond it the ones changed longest ago are forgotten. */
export const maximumBrowserPanes = 256;

const clamp = (width: number) => Math.min(maximumBrowserWidth, Math.max(minimumBrowserWidth, Math.round(width)));
const identifier = (value: string) => value.length > 0 && value.length <= 256;

/** What was kept, or nothing for a value that cannot be read. An entry that cannot be read is left out alone. */
export function parseBrowserPanes(text: string | null): BrowserPanes {
  const panes = new Map<string, BrowserPaneState>();
  try {
    const value: unknown = text ? JSON.parse(text) : null;
    if (!value || typeof value !== "object" || Array.isArray(value)) return panes;
    for (const [id, entry] of Object.entries(value)) {
      if (!identifier(id) || !entry || typeof entry !== "object") continue;
      const { open, width } = entry as { open?: unknown; width?: unknown };
      if (typeof open !== "boolean" || typeof width !== "number" || !Number.isFinite(width)) continue;
      panes.set(id.toLowerCase(), { open, width: clamp(width) });
    }
  } catch { return new Map(); }
  return limited(panes);
}

/** The browser part of a session: what was kept for it, or a closed one with the default width. */
export function browserPaneOf(panes: BrowserPanes, sessionId: string): BrowserPaneState {
  return panes.get(sessionId.toLowerCase()) ?? defaultBrowserPane;
}

/**
 * The browser parts with one of them changed. A part that is closed with the default width is what a session
 * has by itself: it is not kept.
 */
export function withBrowserPane(panes: BrowserPanes, sessionId: string, change: Partial<BrowserPaneState>): BrowserPanes {
  const id = sessionId.toLowerCase();
  if (!identifier(id)) return panes;
  const current = browserPaneOf(panes, id);
  const next = { open: change.open ?? current.open, width: clamp(change.width ?? current.width) };
  if (next.open === current.open && next.width === current.width) return panes;
  const changed = new Map(panes);
  changed.delete(id);
  if (next.open || next.width !== defaultBrowserPane.width) changed.set(id, next);
  return limited(changed);
}

/** The width of a browser part after its splitter moved: the splitter is on its left, so a move to the left widens it. */
export function resizeBrowserWidth(width: number, delta: number): number {
  return clamp(width - delta);
}

/** The browser parts without those of sessions that are gone. `exists` takes the lower-case id of a session. */
export function withoutBrowserPanes(panes: BrowserPanes, exists: (sessionId: string) => boolean): BrowserPanes {
  const kept = new Map([...panes].filter(([id]) => exists(id)));
  return kept.size === panes.size ? panes : kept;
}

export function persistBrowserPanes(save: (value: string) => void, panes: BrowserPanes): boolean {
  try { save(JSON.stringify(Object.fromEntries(panes))); return true; } catch { return false; }
}

/**
 * Whether the browser part takes the whole pane of its session: the pane is too narrow for the least
 * conversation beside the least browser.
 */
export function browserCoversPane(paneWidth: number): boolean {
  return paneWidth > 0 && paneWidth < minimumConversationWidth + minimumBrowserWidth;
}

function limited(panes: Map<string, BrowserPaneState>): Map<string, BrowserPaneState> {
  for (const id of [...panes.keys()].slice(0, Math.max(0, panes.size - maximumBrowserPanes))) panes.delete(id);
  return panes;
}
