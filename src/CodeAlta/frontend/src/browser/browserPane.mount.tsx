import { StrictMode, useState, type CSSProperties } from "react";
import { createRoot } from "react-dom/client";
import type { BrowserAnnotationItem, BrowserAnnotationsItem, BrowserAnnotationsReply, BrowserPageItem, BrowserReply, BrowserSessionItem } from "#neoastra";
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
  pickStill: null as string | null,
  // The last opening of an annotation the pane took.
  taken: 0,
  annotations: null as BrowserAnnotationsItem | null,
  prompt: { status: "ok", text: "The annotations", pictures: [] as number[] },
};
const element = (tag: string, name: string) => ({ selector: `#${name.toLowerCase().replace(/\W+/g, "-")}`, tag, role: "", name, text: name, html: `<${tag}>` });
const annotation = (number: number, more: Partial<BrowserAnnotationItem> = {}): BrowserAnnotationItem => ({ number, text: `Change ${number}`, status: "open", resolvedBy: null, note: null,
  address: "https://example.com/a", title: "Page a", element: element("button", `Button ${number}`), hasImage: false, found: true, ...more });
const annotated = (name: string, ...parts: unknown[]): Promise<BrowserAnnotationsReply> => {
  state.calls.push([name, ...parts].join(" "));
  return Promise.resolve({ status: state.status, annotations: state.annotations });
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
  annotations: () => state.annotations,
  pick: (sessionId, pageId, on) => annotated("pick", sessionId, pageId, on),
  saveAnnotation: (sessionId, text) => annotated("saveAnnotation", sessionId, text),
  discardPick: sessionId => annotated("discardPick", sessionId),
  changeAnnotation: (sessionId, number, action) => annotated("changeAnnotation", sessionId, number, action),
  pickStill: sessionId => { state.calls.push(`pickStill ${sessionId}`); return Promise.resolve(state.pickStill); },
  takeOpened: () => {
    const opened = state.annotations?.opened ?? null;
    if (!opened || opened.sequence <= state.taken) return null;
    state.taken = opened.sequence;
    return opened;
  },
  annotationImage: (sessionId, number) => { state.calls.push(`annotationImage ${sessionId} ${number}`); return Promise.resolve(state.still); },
  annotationPrompt: sessionId => { state.calls.push(`annotationPrompt ${sessionId}`); return Promise.resolve(state.prompt); },
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
    setAnnotations: (annotations: BrowserAnnotationsItem | null) => { state.annotations = annotations; changed(); },
    page, annotation, element } });
  return <ShellLanguageContext.Provider value={{ locale, choice: locale, setLanguage: value => setLocale(value as Locale) }}>
    <div className="ide-shell" style={{ display: "block", ...size, marginLeft: 13.5, marginTop: 7.5 }}>
      <div className="session-pane-row" data-browser="beside" style={{ "--browser-width": "480px" } as CSSProperties}>
        {/* The composer of the session, as far as the browser part uses it: an editor that takes a prompt and pasted images. */}
        <div className="session-workspace"><textarea id="session-prompt-fixture" aria-label="Message" style={{ width: 200, height: 40 }} /></div>
        <div className="pane-splitter browser-splitter" role="separator"><span /></div>
        {mounted && <BrowserPane hub={hub} occlusion={occlusion} sessionId="fixture" shown={shown} onClose={() => setClosed(value => value + 1)} />}
      </div>
    </div>
  </ShellLanguageContext.Provider>;
}
createRoot(document.getElementById("root")!).render(<StrictMode><Fixture /></StrictMode>);
