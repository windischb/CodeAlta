import { Button } from "@blueprintjs/core";
import { useEffect, useRef, useState, useSyncExternalStore, type KeyboardEvent } from "react";
import type { BrowserElementPlaceItem, BrowserPageItem } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import { addAnnotationsToPrompt } from "./annotationPrompt";
import { AnnotationPopup } from "./AnnotationPopup";
import { annotationNotice, BrowserAnnotations } from "./BrowserAnnotations";
import { BrowserDialog } from "./BrowserDialog";
import type { BrowserHub, BrowserStill } from "./browserHub";
import { deviceBounds, sameBounds, type BrowserBounds } from "./browserPlacement";
import type { NativeViewOcclusion } from "./nativeViewOcclusion";

/** How long the page waits between two pictures of a page: a picture takes about a tenth of a second. */
export const stillMilliseconds = 1000;
/** How long a page that finished loading is given to draw itself before its picture is taken. */
export const settledMilliseconds = 300;
/** How long a page that was scrolled to the element of an annotation is given to draw itself before its picture is taken. */
export const scrolledMilliseconds = 120;

/** The popup of an annotation: for the element that was picked, or for an annotation that was opened. */
type Popup = Readonly<{ kind: "pick"; key: string } | { kind: "open"; number: number; sequence: number; place: BrowserElementPlaceItem | null }>;

/** What a reply that is not `ok` says, in words; nothing for a reply that needs none. */
export function browserNotice(status: string): MessageKey | null {
  switch (status) {
    case "ok": case "not_found": return null;
    case "failed": return "The page could not be shown. Try again.";
    case "limit": return "This session has too many pages. Close one to open another.";
    case "invalid": return "This address cannot be opened. The browser opens http and https addresses.";
    case "unsupported": return "This system cannot show a browser inside the window.";
    default: return "The browser is not available right now.";
  }
}

/** What the address field shows for a page: nothing for the empty page. */
export const shownAddress = (page: BrowserPageItem | null | undefined) => !page || page.address === "about:blank" ? "" : page.address;

/**
 * The browser of a session, beside its conversation: the strip of its pages, the toolbar, and the place of
 * the page in front. The page itself is not drawn here. It is a native view of the host, which lies above
 * the window's own page: this component leaves an empty placeholder and says where it is. `shown` is true
 * only while the pane of the session is the one the window shows.
 *
 * A page that waits in a JavaScript dialog shows nothing by itself: the placeholder shows the dialog, and the
 * view is hidden meanwhile, since it would cover it. So it is for the popup of an annotation, drawn at its element
 * over a still picture of the page: the picture the host took at the pick, or one taken before the view is hidden.
 */
export function BrowserPane({ hub, occlusion, sessionId, shown, onClose, onActivate }: {
  hub: BrowserHub; occlusion: NativeViewOcclusion; sessionId: string; shown: boolean;
  onClose: () => void; onActivate?: () => void;
}) {
  const { t } = useShellLanguage();
  const session = useSyncExternalStore(hub.subscribe, () => hub.session(sessionId));
  const supported = useSyncExternalStore(hub.subscribe, hub.supported);
  const pages = session?.pages ?? [];
  const front = pages.find(page => page.id === session?.activePageId) ?? null;
  const frontId = front?.id ?? null;
  const annotations = useSyncExternalStore(hub.subscribe, () => hub.annotations(sessionId));
  // Whether the list of annotations is shown under the page.
  const [listOpen, setListOpen] = useState(false);
  const picking = !!frontId && annotations?.pickingPageId === frontId;
  const pending = annotations?.pending ?? null;
  // The popup of an annotation, its still picture, and whether the view is hidden for it: at once for a pick,
  // whose picture the host took; for an annotation that is opened, once its picture is taken.
  const [popup, setPopup] = useState<Popup | null>(null);
  const [popupStill, setPopupStill] = useState<string | null>(null);
  const [popupHides, setPopupHides] = useState(false);
  const popupShown = !!popup && popupHides;
  const popupNow = useRef(popup);
  popupNow.current = popup;
  const pendingNow = useRef(pending);
  pendingNow.current = pending;
  const [draft, setDraft] = useState<Readonly<{ page: string | null; text: string }> | null>(null);
  const [notice, setNotice] = useState<MessageKey | null>(null);
  // Whether the view is hidden because something of the window is drawn over its place.
  const [covered, setCovered] = useState(false);
  const [still, setStill] = useState<Readonly<{ page: string } & BrowserStill> | null>(null);
  const placeholder = useRef<HTMLDivElement>(null);
  const addressField = useRef<HTMLInputElement>(null);
  const strip = useRef<HTMLDivElement>(null);
  // What the loops below read without starting again: the page in front, and whether its view can be seen.
  const frontPage = useRef(frontId);
  frontPage.current = frontId;
  // Whether the place of the page shows the dialog its page waits in, instead of the page.
  const asking = !!front?.dialog;
  const dialogShown = useRef(asking);
  dialogShown.current = asking;
  const popupHiding = useRef(popupShown);
  popupHiding.current = popupShown;
  // The page whose dialog was answered here: it takes the keyboard back once its view is shown again.
  const answered = useRef<string | null>(null);
  const pane = useRef<HTMLElement>(null);
  // What this pane is to who keeps the toasts of the window beside the browsers.
  const [reservation] = useState(() => ({}));
  const visible = useRef(false);
  const lastStill = useRef(Number.NEGATIVE_INFINITY);

  useEffect(() => { hub.ensure(sessionId); }, [hub, sessionId]);
  useEffect(() => { setDraft(null); setNotice(null); setStill(null); setListOpen(false); setPopup(null); setPopupHides(false); setPopupStill(null); }, [sessionId]);

  const closePopup = () => { setPopup(null); setPopupHides(false); setPopupStill(null); };
  // An element that was picked opens the popup over the picture of the page the host took at the pick, and the
  // popup goes with it once it is saved or dropped.
  const pickKey = pending ? `${pending.pageId} ${pending.element.selector} ${pending.place.x},${pending.place.y}` : "";
  const pickStill = !!pending?.hasStill;
  useEffect(() => {
    if (!pickKey) {
      if (popupNow.current?.kind === "pick") closePopup();
      return;
    }
    let current = true;
    setPopup({ kind: "pick", key: pickKey });
    setPopupHides(true);
    setPopupStill(null);
    if (pickStill) void hub.pickStill(sessionId).then(image => { if (current) setPopupStill(image); });
    return () => { current = false; };
  }, [hub, sessionId, pickKey, pickStill]);
  // An annotation that was opened, from the badge of its mark or from the list, opens the popup once a picture of
  // the page is taken, while its view is still shown: the page scrolled to it a moment ago.
  const openedSequence = annotations?.opened?.sequence ?? 0;
  useEffect(() => {
    const opened = hub.takeOpened(sessionId);
    if (!opened || pendingNow.current) return;
    setPopup({ kind: "open", number: opened.number, sequence: opened.sequence, place: opened.place });
    setPopupStill(null);
    const page = frontPage.current;
    if (!page || !visible.current) { setPopupHides(true); return; }
    setPopupHides(false);
    const still = (image: string | null) => {
      const now = popupNow.current;
      if (now?.kind !== "open" || now.sequence !== opened.sequence) return;
      setPopupStill(image);
      setPopupHides(true);
    };
    setTimeout(() => void hub.capture(sessionId, page).then(picture => still(picture?.image ?? null)), scrolledMilliseconds);
  }, [hub, sessionId, openedSequence]);
  // Another page in front, or an annotation that is gone, closes the popup of an annotation.
  const openedNumber = popup?.kind === "open" ? popup.number : null;
  const openedAnnotation = openedNumber === null ? null : annotations?.annotations.find(item => item.number === openedNumber) ?? null;
  useEffect(() => { if (popupNow.current?.kind === "open") closePopup(); }, [frontId]);
  useEffect(() => { if (openedNumber !== null && annotations && !openedAnnotation) closePopup(); }, [openedNumber, annotations, openedAnnotation]);

  // Where the page in front is shown. The placeholder moves without changing its size (another splitter, the
  // Explorer), and an overlay moves without the document changing, so both are looked at once for each frame
  // while the browser is shown. The hub sends a placement only when it changed.
  useEffect(() => {
    if (!shown || supported === false) return;
    let frame = 0;
    let placed: BrowserBounds | null | undefined;
    const look = () => {
      const element = placeholder.current;
      // The toasts of the window stay beside this part while it has a page, shown or hidden for a moment.
      occlusion.reserve(reservation, element && pane.current ? pane.current.getBoundingClientRect() : null);
      const box = element && frontPage.current && !dialogShown.current && !popupHiding.current ? element.getBoundingClientRect() : null;
      const hidden = !!box && occlusion.occludes(box, element);
      const bounds = box && !hidden ? deviceBounds(box, window.devicePixelRatio) : null;
      visible.current = !!bounds;
      setCovered(hidden);
      if (placed !== undefined && sameBounds(placed, bounds)) return;
      placed = bounds;
      hub.place(sessionId, bounds);
      // The page whose dialog the user answered is typed in again, as it was before it asked.
      if (bounds && answered.current) {
        if (answered.current === frontPage.current) void hub.command(sessionId, answered.current, "focus");
        answered.current = null;
      }
    };
    const loop = () => { look(); frame = requestAnimationFrame(loop); };
    const unsubscribe = occlusion.subscribe(look);
    loop();
    return () => {
      cancelAnimationFrame(frame);
      unsubscribe();
      occlusion.reserve(reservation, null);
      visible.current = false;
      hub.place(sessionId, null);
    };
  }, [hub, occlusion, reservation, sessionId, shown, supported]);

  // A picture of the page in front, to draw in its place while the view is hidden behind something of the
  // window. A picture needs a view that is shown and takes a moment, so it is taken ahead of time: when the
  // pointer or the keyboard comes back from the view to the window, and after a page finished loading.
  const takeStill = useRef(() => { });
  takeStill.current = () => {
    const page = frontPage.current, now = performance.now();
    if (!page || !visible.current || now - lastStill.current < stillMilliseconds) return;
    lastStill.current = now;
    void hub.capture(sessionId, page).then(picture => {
      if (picture && frontPage.current === page) setStill({ page, ...picture });
    });
  };
  useEffect(() => {
    if (!shown) return;
    // Over the view the window gets no pointer event: the next one it gets says the pointer is back.
    let away = false;
    const left = (event: PointerEvent) => { if (event.relatedTarget === null) away = true; };
    const back = () => { if (!away) return; away = false; takeStill.current(); };
    const blurred = () => { away = true; };
    document.addEventListener("pointerout", left, true);
    document.addEventListener("pointerover", back, true);
    window.addEventListener("blur", blurred);
    window.addEventListener("focus", back);
    return () => {
      document.removeEventListener("pointerout", left, true);
      document.removeEventListener("pointerover", back, true);
      window.removeEventListener("blur", blurred);
      window.removeEventListener("focus", back);
    };
  }, [shown]);
  const loading = front?.loading ?? false;
  useEffect(() => {
    if (!shown || !frontId || loading) return;
    const timer = setTimeout(() => takeStill.current(), settledMilliseconds);
    return () => clearTimeout(timer);
  }, [shown, frontId, loading, front?.address]);

  const said = (status: string) => { setNotice(browserNotice(status)); return status === "ok"; };
  const focusAddress = () => { addressField.current?.focus(); addressField.current?.select(); };
  async function go(text: string) {
    const address = text.trim();
    if (!address) return;
    setDraft(null);
    const reply = frontId ? await hub.navigate(sessionId, frontId, address) : await hub.open(sessionId, address);
    const page = frontId ?? reply.pageId;
    // What is typed next goes to the page.
    if (said(reply.status) && page) void hub.command(sessionId, page, "focus");
  }
  async function openPage() {
    if (said((await hub.open(sessionId)).status)) focusAddress();
  }
  const select = (page: string) => { if (page !== frontId) void hub.select(sessionId, page).then(reply => said(reply.status)); };
  const close = (page: string) => void hub.close(sessionId, page).then(reply => said(reply.status));
  const answerDialog = (page: string, accept: boolean, text: string | null) => {
    answered.current = page;
    return hub.answerDialog(sessionId, page, accept, text).then(reply => said(reply.status));
  };
  const annotationSaid = (status: string) => { setNotice(annotationNotice(status)); return status === "ok"; };
  const togglePick = () => { if (frontId) void hub.pick(sessionId, frontId, !picking).then(reply => annotationSaid(reply.status)); };
  async function addToPrompt() {
    switch (await addAnnotationsToPrompt(hub, sessionId, pane.current?.closest(".session-pane-row") ?? null)) {
      case "ok": setNotice("The annotations were added to the prompt."); break;
      case "text": setNotice("The annotations were added to the prompt without their pictures."); break;
      case "no_composer": setNotice("This session has no prompt to add the annotations to."); break;
      case "failed": setNotice("The browser is not available right now."); break;
      case "none": break;
    }
  }
  const ask = (command: "back" | "forward" | "reload" | "stop" | "devtools") => {
    if (frontId) void hub.command(sessionId, frontId, command).then(reply => said(reply.status));
  };

  function addressKey(event: KeyboardEvent<HTMLInputElement>) {
    if (event.nativeEvent.isComposing) return;
    if (event.key === "Enter") { event.preventDefault(); void go(event.currentTarget.value); }
    else if (event.key === "Escape") {
      event.preventDefault(); event.stopPropagation();
      setDraft(null);
      const field = event.currentTarget;
      requestAnimationFrame(() => field.select());
    }
  }
  // The strip is a tab list: the arrows go from page to page, and with Ctrl+Shift they move the page.
  function tabKey(event: KeyboardEvent<HTMLButtonElement>, index: number) {
    const step = event.key === "ArrowLeft" ? -1 : event.key === "ArrowRight" ? 1 : 0;
    const target = event.key === "Home" ? 0 : event.key === "End" ? pages.length - 1 : index + step;
    if (event.key === "Delete") { event.preventDefault(); close(pages[index].id); return; }
    if (target === index || target < 0 || target >= pages.length || event.altKey || event.metaKey) return;
    if (event.ctrlKey !== event.shiftKey) return;
    event.preventDefault();
    if (event.ctrlKey) { void hub.move(sessionId, pages[index].id, target).then(reply => said(reply.status)); return; }
    select(pages[target].id);
    strip.current?.querySelectorAll<HTMLElement>('[role="tab"]')[target]?.focus();
  }

  const address = draft && draft.page === frontId ? draft.text : shownAddress(front);
  const field = (className: string) => <input ref={addressField} className={className} type="text" inputMode="url" spellCheck={false} autoComplete="off"
    aria-label={t("Address of the page")} placeholder={t("Type an address")} value={address}
    onChange={event => setDraft({ page: frontId, text: event.target.value })} onFocus={event => event.currentTarget.select()} onKeyDown={addressKey} />;
  const closeButton = <Button variant="minimal" size="small" className="browser-close" icon={<AppIcon name="close" size={15} />}
    aria-label={t("Close the browser")} title={t("Close the browser")} onClick={onClose} />;
  const title = (page: BrowserPageItem) => page.title || shownAddress(page) || t("New page");

  return <section className="browser-pane" ref={pane} aria-label={t("Browser")} data-shown={shown} onFocusCapture={onActivate} onPointerDownCapture={onActivate}>
    {supported === false
      ? <div className="browser-bar">
        <p className="browser-unsupported" role="status">{t("This system cannot show a browser inside the window.")}</p>
        {closeButton}
      </div>
      : <>
        <div className="browser-strip">
          <div className="browser-tabs" role="tablist" aria-label={t("Pages of the browser")} ref={strip}>
            {pages.map((page, index) => <div key={page.id} className="browser-tab" role="presentation" data-front={page.id === frontId}
              onMouseDown={event => { if (event.button === 1) event.preventDefault(); }}
              onAuxClick={event => { if (event.button === 1) { event.preventDefault(); close(page.id); } }}>
              <button type="button" role="tab" aria-selected={page.id === frontId} tabIndex={page.id === frontId ? 0 : -1} title={`${title(page)}\n${shownAddress(page)}`.trim()}
                onClick={() => select(page.id)} onKeyDown={event => tabKey(event, index)}>
                {page.loading ? <ActivitySpinner size={11} label={t("Loading…")} /> : <AppIcon name="globe" size={12} />}
                <span>{title(page)}</span>
                {page.dialog && page.id !== frontId && <span className="browser-tab-mark" role="img" aria-label={t("This page asks a question")} title={t("This page asks a question")} />}
              </button>
              <button type="button" className="browser-tab-close" tabIndex={-1} aria-label={`${t("Close page")}: ${title(page)}`} title={t("Close page")} onClick={() => close(page.id)}>
                <AppIcon name="close" size={12} />
              </button>
            </div>)}
          </div>
          <Button variant="minimal" size="small" className="browser-new" icon={<AppIcon name="plus" size={15} />} aria-label={t("New page")} title={t("New page")}
            onClick={() => void openPage()} />
        </div>
        <div className="browser-bar">
          <Button variant="minimal" size="small" icon={<AppIcon name="arrowLeft" size={15} />} disabled={!front?.canGoBack} aria-label={t("Go back")} title={t("Go back")} onClick={() => ask("back")} />
          <Button variant="minimal" size="small" icon={<AppIcon name="arrowRight" size={15} />} disabled={!front?.canGoForward} aria-label={t("Go forward")} title={t("Go forward")} onClick={() => ask("forward")} />
          {loading
            ? <Button variant="minimal" size="small" icon={<AppIcon name="stop" size={14} />} aria-label={t("Stop loading")} title={t("Stop loading")} onClick={() => ask("stop")} />
            : <Button variant="minimal" size="small" icon={<AppIcon name="refresh" size={15} />} disabled={!front} aria-label={t("Reload")} title={t("Reload")} onClick={() => ask("reload")} />}
          {front ? field("browser-address") : <span className="browser-bar-space" />}
          <Button variant="minimal" size="small" className="browser-annotate" icon={<AppIcon name="locate" size={15} />} disabled={!front || asking} active={picking} aria-pressed={picking}
            aria-label={t(picking ? "Stop annotating" : "Annotate an element of the page")} title={t(picking ? "Stop annotating" : "Annotate an element of the page")} onClick={togglePick}>
            {!!annotations?.open && <span className="browser-count">{annotations.open}</span>}
          </Button>
          <Button variant="minimal" size="small" icon={<AppIcon name="list" size={15} />} active={listOpen} aria-pressed={listOpen}
            aria-label={t("Show or hide the annotations")} title={t("Show or hide the annotations")} onClick={() => setListOpen(open => !open)} />
          <Button variant="minimal" size="small" icon={<AppIcon name="code" size={15} />} disabled={!front} aria-label={t("Open developer tools")} title={t("Open developer tools")} onClick={() => ask("devtools")} />
          {closeButton}
        </div>
        {notice && <p className="browser-notice" role="status">{t(notice)}</p>}
        {picking && <p className="browser-notice browser-picking" role="status">{t("Click an element of the page to annotate it. Escape ends.")}</p>}
        {front
          ? <div className="browser-view" ref={placeholder} data-covered={covered} data-dialog={asking} data-popup={!asking && popupShown}>
            {/* Drawn only while the view is hidden: the view itself covers this place otherwise. */}
            {front.dialog && <BrowserDialog key={`${front.id} ${front.dialog.kind} ${front.dialog.message}`} dialog={front.dialog} address={front.address} pane={pane.current}
              onAnswer={(accept, text) => answerDialog(front.id, accept, text)} />}
            {!front.dialog && popup && popupShown && <>
              {popupStill
                ? <img className="browser-still" src={popupStill} alt="" aria-hidden />
                : <div className="browser-covered"><strong>{title(front)}</strong><span>{shownAddress(front)}</span></div>}
              {popup.kind === "pick" && pending
                ? <AnnotationPopup key={popup.key} hub={hub} sessionId={sessionId} subject={{ kind: "pick", element: pending.element }} place={pending.place} onNotice={annotationSaid} onClose={closePopup} />
                : popup.kind === "open" && openedAnnotation
                  ? <AnnotationPopup key={popup.sequence} hub={hub} sessionId={sessionId} subject={{ kind: "open", annotation: openedAnnotation }} place={popup.place} onNotice={annotationSaid} onClose={closePopup} />
                  : null}
            </>}
            {!front.dialog && !popupShown && covered && (still?.page === front.id
              ? <img className="browser-still" src={still.image} alt="" aria-hidden />
              : <div className="browser-covered">
                <strong>{title(front)}</strong>
                <span>{shownAddress(front)}</span>
                <small>{t("The page is hidden while something is shown over it.")}</small>
              </div>)}
          </div>
          : <div className="browser-empty">
            <AppIcon name="globe" size={28} />
            {field("browser-address browser-address-first")}
            <p>{t("This session has no page yet. Type an address and press Enter to open one.")}</p>
          </div>}
        {listOpen && <BrowserAnnotations hub={hub} sessionId={sessionId} annotations={annotations} onNotice={annotationSaid}
          onAddToPrompt={() => void addToPrompt()} onClose={() => setListOpen(false)} />}
      </>}
  </section>;
}
