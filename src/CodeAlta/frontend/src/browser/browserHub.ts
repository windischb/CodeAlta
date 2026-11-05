// The page's link to the built-in browser of the sessions: the browser tabs each session has, and where this window
// shows the browser of a session.
import type { browser, BrowserReply, BrowserSessionItem } from "#neoastra";

export type BrowserApi = Pick<typeof browser, "watch" | "state" | "open" | "close" | "select" | "move" | "navigate" | "command" | "place" | "capture">;
/** What a browser tab can be asked to do besides loading an address. */
export type BrowserCommand = "back" | "forward" | "reload" | "stop" | "focus";
/** Where this window shows the browser of a session: a rectangle of its page, in CSS pixels. */
export type BrowserBox = Readonly<{ x: number; y: number; width: number; height: number }>;
/** A picture of a browser tab: a `data:` address and its size in pixels. */
export type BrowserStill = Readonly<{ image: string; width: number; height: number }>;
type Timers = Readonly<{ set: (run: () => void, milliseconds: number) => unknown; clear: (timer: unknown) => void }>;

/** How long the page waits before it listens again after the host stopped telling. */
export const reconnectMilliseconds = 2000;
/** How long a call may take: a view that has to be created is the slow one. */
const callMilliseconds = 20_000;

export type BrowserHub = ReturnType<typeof createBrowserHub>;

/** Whether two places are the same one, to the quarter of a CSS pixel: the host is told a place once. */
export function sameBox(a: BrowserBox | null, b: BrowserBox | null): boolean {
  const near = (x: number, y: number) => Math.abs(x - y) < 0.25;
  return a === b || (!!a && !!b && near(a.x, b.x) && near(a.y, b.y) && near(a.width, b.width) && near(a.height, b.height));
}

/** A rectangle that can be a place: finite, with an area. */
export function boxOf(rect: Readonly<{ left: number; top: number; width: number; height: number }>): BrowserBox | null {
  const box = { x: rect.left, y: rect.top, width: rect.width, height: rect.height };
  return [box.x, box.y, box.width, box.height].every(Number.isFinite) && box.width >= 1 && box.height >= 1 ? box : null;
}

function sameSession(a: BrowserSessionItem | undefined, b: BrowserSessionItem): boolean {
  return !!a && a.activePageId === b.activePageId && a.window === b.window && a.pages.length === b.pages.length && a.pages.every((page, index) => {
    const other = b.pages[index];
    return page.id === other.id && page.address === other.address && page.title === other.title && page.loading === other.loading
      && page.canGoBack === other.canGoBack && page.canGoForward === other.canGoForward && page.live === other.live;
  });
}

/**
 * Listens to the host for the page. One watch carries the browser of every session that was asked for. Placing the
 * browser of a session is the frequent call: one call at a time for each session, and of several places asked for in
 * a row the last one counts.
 */
export function createBrowserHub(api: BrowserApi, timers: Timers = { set: (run, milliseconds) => setTimeout(run, milliseconds), clear: timer => clearTimeout(timer as number) }) {
  let epoch: string | null = null;
  let supported: boolean | null = null;
  // Whether the watch of this epoch said it is there: the host is asked about a session only then.
  let listening = false;
  const sessions = new Map<string, BrowserSessionItem>();
  const listeners = new Set<() => void>();
  // The sessions whose browser this window shows, and those the host was asked about since it is listened to.
  const asked = new Set<string>();
  const ensured = new Set<string>();
  // Where this window wants the browser of a session (absent: nowhere), what the host was last told, and who is being told.
  const wanted = new Map<string, BrowserBox>();
  const told = new Map<string, BrowserBox | null>();
  const placing = new Set<string>();

  const notify = () => { for (const listener of [...listeners]) listener(); };

  function apply(session: BrowserSessionItem | null | undefined) {
    if (!session) return;
    if (sameSession(sessions.get(session.sessionId), session)) return;
    sessions.set(session.sessionId, session);
    notify();
  }

  async function call(sessionId: string, send: (expectedEpoch: string) => Promise<BrowserReply>): Promise<BrowserReply> {
    const host = epoch;
    if (!host) return { status: "unavailable", session: null, pageId: null };
    try {
      const reply = await send(host);
      // What the host of another epoch said is about browsers that are gone.
      if (epoch === host) apply(reply.session);
      return reply;
    } catch { return { status: "failed", session: sessions.get(sessionId) ?? null, pageId: null }; }
  }

  function ask(id: string) {
    const host = epoch;
    if (!host || !listening || ensured.has(id)) return;
    ensured.add(id);
    api.state({ expectedEpoch: host, sessionId: id }, { timeoutMilliseconds: callMilliseconds }).then(reply => {
      if (epoch !== host) return;
      if (reply.status === "ok") apply(reply.session); else ensured.delete(id);
    }, () => { if (epoch === host) ensured.delete(id); });
  }

  const hidden = (expectedEpoch: string, sessionId: string) => ({ expectedEpoch, sessionId, visible: false, x: 0, y: 0, width: 0, height: 0 });

  async function tell(id: string) {
    if (placing.has(id)) return;
    placing.add(id);
    try {
      for (let host = epoch; host; host = epoch) {
        const place = wanted.get(id) ?? null;
        if (told.has(id) && sameBox(told.get(id) ?? null, place)) break;
        told.set(id, place);
        let status: string;
        try {
          status = (await api.place(place ? { expectedEpoch: host, sessionId: id, visible: true, ...place } : hidden(host, id), { timeoutMilliseconds: callMilliseconds })).status;
        } catch { status = "failed"; }
        if (epoch !== host) break;
        if (status === "ok") continue;
        // The host did not take the place, and where it shows the browser is not known: it is asked to hide it.
        if (place) void api.place(hidden(host, id)).catch(() => { /* The host is gone, and its views with it. */ });
        break;
      }
    } finally { placing.delete(id); }
  }

  function tellAll() {
    const ids = new Set([...told.keys(), ...wanted.keys()]);
    told.clear();
    for (const id of ids) void tell(id);
  }

  return {
    /** Calls `listener` when the browser of a session changes, or what is known of the system. */
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    /** Whether this system has the built-in browser; null until the host said. */
    supported: () => supported,
    /** The browser of a session as the host last told it, or null before it did. The same object until it changes. */
    session: (sessionId: string): BrowserSessionItem | null => sessions.get(sessionId) ?? null,

    /** Listens to the host of an epoch until the returned function is called. */
    connect(hostEpoch: string) {
      const abort = new AbortController();
      let timer: unknown = null;
      epoch = hostEpoch;
      listening = false;
      ensured.clear();
      told.clear();
      async function watch() {
        try {
          for await (const event of await api.watch({ expectedEpoch: hostEpoch }, { signal: abort.signal })) {
            if (abort.signal.aborted) return;
            if (event.kind === "ready") {
              if (supported !== event.supported) { supported = event.supported; notify(); }
              // The host is there, or there again: it is asked for the browsers this window shows, and told where.
              listening = true;
              ensured.clear();
              for (const id of asked) ask(id);
              tellAll();
            } else if (event.kind === "session") apply(event.session);
          }
        } catch { /* The watch ended: it starts again below, unless the page stopped listening. */ }
        if (epoch === hostEpoch) listening = false;
        if (!abort.signal.aborted) timer = timers.set(() => void watch(), reconnectMilliseconds);
      }
      void watch();
      return () => {
        abort.abort();
        timers.clear(timer);
        if (epoch !== hostEpoch) return;
        // The page stops listening while its host may still run: no view stays where nothing draws its frame.
        for (const [id, place] of told) if (place) void api.place(hidden(hostEpoch, id)).catch(() => { /* The host is gone. */ });
        told.clear();
        ensured.clear();
        listening = false;
        epoch = null;
      };
    },

    /** Asks the host for the browser of a session, once while it is listened to. */
    ensure(sessionId: string) {
      asked.add(sessionId);
      ask(sessionId);
    },

    /** Says where this window shows the browser of a session, or that it no longer shows it. */
    place(sessionId: string, box: BrowserBox | null) {
      if (box) wanted.set(sessionId, box); else wanted.delete(sessionId);
      void tell(sessionId);
    },

    open: (sessionId: string, address: string | null) => call(sessionId, expectedEpoch => api.open({ expectedEpoch, sessionId, address }, { timeoutMilliseconds: callMilliseconds })),
    close: (sessionId: string, pageId: string) => call(sessionId, expectedEpoch => api.close({ expectedEpoch, sessionId, pageId }, { timeoutMilliseconds: callMilliseconds })),
    select: (sessionId: string, pageId: string) => call(sessionId, expectedEpoch => api.select({ expectedEpoch, sessionId, pageId }, { timeoutMilliseconds: callMilliseconds })),
    move: (sessionId: string, pageId: string, index: number) => call(sessionId, expectedEpoch => api.move({ expectedEpoch, sessionId, pageId, index }, { timeoutMilliseconds: callMilliseconds })),
    navigate: (sessionId: string, pageId: string, address: string) => call(sessionId, expectedEpoch => api.navigate({ expectedEpoch, sessionId, pageId, address }, { timeoutMilliseconds: callMilliseconds })),
    command: (sessionId: string, pageId: string, command: BrowserCommand) => call(sessionId, expectedEpoch => api.command({ expectedEpoch, sessionId, pageId, command }, { timeoutMilliseconds: callMilliseconds })),

    /** A picture of a browser tab, or null when there is none. */
    async capture(sessionId: string, pageId: string): Promise<BrowserStill | null> {
      const host = epoch;
      if (!host) return null;
      try {
        const reply = await api.capture({ expectedEpoch: host, sessionId, pageId }, { timeoutMilliseconds: callMilliseconds });
        return reply.status === "ok" && reply.image ? { image: reply.image, width: reply.width, height: reply.height } : null;
      } catch { return null; }
    },
  };
}
