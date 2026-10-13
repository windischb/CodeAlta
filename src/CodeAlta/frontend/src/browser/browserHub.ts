// The page's link to the browsers of the sessions: the pages each session has, and where the window shows them.
import type { browser, BrowserReply, BrowserSessionItem } from "#neoastra";
import { sameBounds, type BrowserBounds } from "./browserPlacement";

export type BrowserApi = Pick<typeof browser, "watch" | "state" | "open" | "close" | "select" | "move" | "navigate" | "command" | "answerDialog" | "place" | "release" | "capture">;
/** What a page can be asked to do. */
export type BrowserCommand = "back" | "forward" | "reload" | "stop" | "devtools" | "focus";
/** A picture of a page: a `data:` address and its size in pixels. */
export type BrowserStill = Readonly<{ image: string; width: number; height: number }>;
type Timers = Readonly<{ set: (run: () => void, milliseconds: number) => unknown; clear: (timer: unknown) => void }>;

/** How long the page waits before it listens again after the host stopped telling. */
export const reconnectMilliseconds = 2000;
/** How long a call may take: a view that has to be created is the slow one. */
const callMilliseconds = 20_000;

export type BrowserHub = ReturnType<typeof createBrowserHub>;

// The host knows a session by its id as it is written: the page gives it back unchanged.
const key = (sessionId: string) => sessionId;

function sameSession(a: BrowserSessionItem | undefined, b: BrowserSessionItem): boolean {
  return !!a && a.activePageId === b.activePageId && a.pages.length === b.pages.length && a.pages.every((page, index) => {
    const other = b.pages[index];
    return page.id === other.id && page.address === other.address && page.title === other.title && page.loading === other.loading
      && page.canGoBack === other.canGoBack && page.canGoForward === other.canGoForward && page.live === other.live
      && page.dialog?.kind === other.dialog?.kind && page.dialog?.message === other.dialog?.message && page.dialog?.defaultText === other.dialog?.defaultText;
  });
}

/**
 * Listens to the host for the page. One watch carries the browser of every session that was asked for. The
 * placement of a view is the frequent call: for each session one call at a time, and of several placements
 * asked for in a row the last one counts.
 */
export function createBrowserHub(api: BrowserApi, timers: Timers = { set: (run, milliseconds) => setTimeout(run, milliseconds), clear: timer => clearTimeout(timer as number) }) {
  let epoch: string | null = null;
  let supported: boolean | null = null;
  // Whether the watch of this epoch said it is there: the host is asked about a session only then.
  let listening = false;
  const sessions = new Map<string, BrowserSessionItem>();
  const listeners = new Set<() => void>();
  // The sessions whose browser the page shows, and those the host was asked about since it is listened to.
  const asked = new Set<string>();
  const ensured = new Set<string>();
  // Where the page wants the view of a session (absent: nowhere), what the host was last told, and who is being told.
  const wanted = new Map<string, BrowserBounds>();
  const told = new Map<string, BrowserBounds | null>();
  const placing = new Set<string>();

  const notify = () => { for (const listener of [...listeners]) listener(); };
  const quietly = (call: Promise<unknown>) => void call.catch(() => { /* The host is gone: its views are gone with it. */ });

  function apply(session: BrowserSessionItem | null | undefined) {
    if (!session) return;
    const id = key(session.sessionId);
    if (sameSession(sessions.get(id), session)) return;
    sessions.set(id, session);
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
    } catch { return { status: "failed", session: sessions.get(key(sessionId)) ?? null, pageId: null }; }
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
        if (told.has(id) && sameBounds(told.get(id) ?? null, place)) break;
        told.set(id, place);
        let status: string;
        try {
          status = (await api.place(place ? { expectedEpoch: host, sessionId: id, visible: true, ...place } : hidden(host, id), { timeoutMilliseconds: callMilliseconds })).status;
        } catch { status = "failed"; }
        if (epoch !== host) break;
        if (status === "ok") continue;
        // The host did not take the placement, and where it shows the view is not known: it is asked to hide
        // it. The same placement is not tried again; another one is, and so is this one when the host is back.
        if (place) quietly(api.place(hidden(host, id)));
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
    /** Whether this system shows a browser inside the window; null until the host said. */
    supported: () => supported,
    /** The browser of a session as the host last told it, or null before it did. The same object until it changes. */
    session: (sessionId: string): BrowserSessionItem | null => sessions.get(key(sessionId)) ?? null,

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
              // The host is there, or there again: it is asked for the browsers the page shows, and told where.
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
        for (const [id, place] of told) if (place) quietly(api.place(hidden(hostEpoch, id)));
        told.clear();
        ensured.clear();
        listening = false;
        epoch = null;
      };
    },

    /**
     * The page shows the browser of a session: the host is asked for it once for each time it is listened to,
     * which also makes it bring back the pages kept for the session.
     */
    ensure(sessionId: string) {
      const id = key(sessionId);
      asked.add(id);
      ask(id);
    },
    /** Opens a page and puts it in front; without an address, the empty page. */
    open: (sessionId: string, address: string | null = null) =>
      call(sessionId, expectedEpoch => api.open({ expectedEpoch, sessionId, address }, { timeoutMilliseconds: callMilliseconds })),
    /** Closes a page. */
    close: (sessionId: string, pageId: string) => call(sessionId, expectedEpoch => api.close({ expectedEpoch, sessionId, pageId })),
    /** Puts a page in front. */
    select: (sessionId: string, pageId: string) =>
      call(sessionId, expectedEpoch => api.select({ expectedEpoch, sessionId, pageId }, { timeoutMilliseconds: callMilliseconds })),
    /** Moves a page to another place of the strip, from 0. */
    move: (sessionId: string, pageId: string, index: number) => call(sessionId, expectedEpoch => api.move({ expectedEpoch, sessionId, pageId, index })),
    /** Loads what was typed as an address; the host reads it. */
    navigate: (sessionId: string, pageId: string, address: string) =>
      call(sessionId, expectedEpoch => api.navigate({ expectedEpoch, sessionId, pageId, address }, { timeoutMilliseconds: callMilliseconds })),
    /** Asks a page to go back or forward, to load again, to stop, to open its developer tools or to take the keyboard. */
    command: (sessionId: string, pageId: string, command: BrowserCommand) =>
      call(sessionId, expectedEpoch => api.command({ expectedEpoch, sessionId, pageId, command })),
    /** Answers the JavaScript dialog a page has open: accepted or dismissed, with the text of a prompt. */
    answerDialog: (sessionId: string, pageId: string, accept: boolean, text: string | null = null) =>
      call(sessionId, expectedEpoch => api.answerDialog({ expectedEpoch, sessionId, pageId, accept, text })),

    /**
     * Says where the pane of a session shows its browser, or with null that it shows none. The same placement
     * is not sent twice, and a session that is not placed is hidden on the host.
     */
    place(sessionId: string, bounds: BrowserBounds | null) {
      const id = key(sessionId);
      if (bounds) wanted.set(id, bounds); else wanted.delete(id);
      // A session that was never placed has no view to hide.
      if (!bounds && !told.has(id)) return;
      void tell(id);
    },
    /** The window no longer shows a session: the host lets its views go, and keeps its pages. */
    release(sessionId: string) {
      const id = key(sessionId), host = epoch;
      asked.delete(id);
      ensured.delete(id);
      wanted.delete(id);
      told.delete(id);
      const known = sessions.delete(id);
      if (host) quietly(api.release({ expectedEpoch: host, sessionId: id }));
      if (known) notify();
    },
    /** A picture of what a page shows, or null when it has none to give: the view must be visible for it. */
    async capture(sessionId: string, pageId: string): Promise<BrowserStill | null> {
      const host = epoch;
      if (!host) return null;
      try {
        const reply = await api.capture({ expectedEpoch: host, sessionId, pageId }, { timeoutMilliseconds: 8000 });
        return reply.status === "ok" && reply.image ? { image: reply.image, width: reply.width, height: reply.height } : null;
      } catch { return null; }
    },
  };
}
