import { StrictMode, useState, type CSSProperties } from "react";
import { createRoot } from "react-dom/client";
import type { BrowserPageItem, BrowserReply, BrowserSessionItem } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import type { Locale } from "../localization";
import { BrowserPane } from "./BrowserPane";
import type { BrowserHub } from "./browserHub";
import type { BrowserBounds } from "./browserPlacement";
import { createNativeViewOcclusion } from "./nativeViewOcclusion";

const page = (id: string, more: Partial<BrowserPageItem> = {}): BrowserPageItem => ({ id, address: `https://example.com/${id}`, title: `Page ${id}`, loading: false, canGoBack: false, canGoForward: false, live: true, dialog: null, ...more });
// A host that keeps what the pane asks and answers as the test says: no view is placed anywhere.
const state = {
  session: { sessionId: "fixture", pages: [page("a", { canGoBack: true }), page("b")], activePageId: "a" } as BrowserSessionItem | null,
  supported: true as boolean | null,
  calls: [] as string[],
  places: [] as (BrowserBounds | null)[],
  status: "ok",
  still: null as string | null,
};
const listeners = new Set<() => void>();
const changed = () => { for (const listener of [...listeners]) listener(); };
const answer = (name: string, ...parts: unknown[]): Promise<BrowserReply> => {
  state.calls.push([name, ...parts].join(" "));
  return Promise.resolve({ status: state.status, session: state.session, pageId: null });
};
const hub: BrowserHub = {
  subscribe: listener => { listeners.add(listener); return () => { listeners.delete(listener); }; },
  supported: () => state.supported,
  session: () => state.session,
  connect: () => () => { },
  ensure: sessionId => { state.calls.push(`ensure ${sessionId}`); },
  open: (sessionId, address = null) => answer("open", sessionId, address ?? "").then(reply => ({ ...reply, pageId: "new" })),
  close: (sessionId, pageId) => answer("close", sessionId, pageId),
  select: (sessionId, pageId) => answer("select", sessionId, pageId),
  move: (sessionId, pageId, index) => answer("move", sessionId, pageId, index),
  navigate: (sessionId, pageId, address) => answer("navigate", sessionId, pageId, address),
  command: (sessionId, pageId, command) => answer("command", sessionId, pageId, command),
  answerDialog: (sessionId, pageId, accept, text = null) => answer("answerDialog", sessionId, pageId, accept ? "accept" : "dismiss", text ?? ""),
  place: (_sessionId, bounds) => { state.places.push(bounds); },
  release: sessionId => { state.calls.push(`release ${sessionId}`); },
  capture: (sessionId, pageId) => { state.calls.push(`capture ${sessionId} ${pageId}`); return Promise.resolve(state.still ? { image: state.still, width: 1, height: 1 } : null); },
};
const occlusion = createNativeViewOcclusion();

// The pane of a session as the window lays it out: the conversation, the splitter, and the browser at its right.
function Fixture() {
  const [shown, setShown] = useState(true);
  const [mounted, setMounted] = useState(true);
  const [locale, setLocale] = useState<Locale>("en");
  const [size, setSize] = useState({ width: 1100, height: 600 });
  const [closed, setClosed] = useState(0);
  Object.assign(window, { browserFixture: { state, setShown, setMounted, setLocale, setSize, closed,
    setSession: (session: BrowserSessionItem | null) => { state.session = session; changed(); },
    setSupported: (supported: boolean | null) => { state.supported = supported; changed(); },
    page } });
  return <ShellLanguageContext.Provider value={{ locale, choice: locale, setLanguage: value => setLocale(value as Locale) }}>
    <div className="ide-shell" style={{ display: "block", ...size, marginLeft: 13.5, marginTop: 7.5 }}>
      <div className="session-pane-row" data-browser="beside" style={{ "--browser-width": "480px" } as CSSProperties}>
        <div className="session-workspace" />
        <div className="pane-splitter browser-splitter" role="separator"><span /></div>
        {mounted && <BrowserPane hub={hub} occlusion={occlusion} sessionId="fixture" shown={shown} onClose={() => setClosed(value => value + 1)} />}
      </div>
    </div>
  </ShellLanguageContext.Provider>;
}
createRoot(document.getElementById("root")!).render(<StrictMode><Fixture /></StrictMode>);
