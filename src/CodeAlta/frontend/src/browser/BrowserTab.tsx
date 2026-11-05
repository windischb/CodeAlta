import { Button, NonIdealState } from "@blueprintjs/core";
import { useEffect, useRef, useState, useSyncExternalStore, type KeyboardEvent } from "react";
import type { BrowserPageItem } from "#neoastra";
import { AppIcon } from "../AppIcon";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import { boxOf, sameBox, type BrowserBox, type BrowserHub, type BrowserStill } from "./browserHub";
import type { NativeViewOcclusion } from "./nativeViewOcclusion";

/** What a reply that is not `ok` says, in words; nothing for a reply that needs none. */
export function browserNotice(status: string): MessageKey | null {
  switch (status) {
    case "ok": case "not_found": return null;
    case "failed": return "The page could not be shown. Try again.";
    case "limit": return "This browser has too many tabs. Close one to open another.";
    case "invalid": return "This address cannot be opened. The browser opens http and https addresses.";
    case "unsupported": return "This system cannot show a browser inside the window.";
    default: return "The browser is not available right now.";
  }
}

/** What the address field shows for a browser tab: nothing for the empty page. */
export const shownAddress = (page: BrowserPageItem | null | undefined) => !page || page.address === "about:blank" ? "" : page.address;

/** What the strip shows for a browser tab: its title, else its address, else that it is new. */
export const pageLabel = (page: BrowserPageItem, empty: string) => page.title || shownAddress(page) || empty;

/**
 * The browser of a session, in a tab of the window: the strip of its browser tabs, the toolbar, and the place of
 * the browser tab in front. The page itself is not drawn here: it is a native view of the host, which lies above the
 * page of the window. This component leaves a placeholder and says where it is, as long as the tab is shown.
 *
 * Something of the window that is drawn over the placeholder (a menu, a dialog, a toast) would be under the view: the
 * view is then hidden, and the placeholder shows a picture of the page instead, which the host takes at once.
 */
export function BrowserTab({ hub, occlusion, sessionId, visible, onActivate }: {
  hub: BrowserHub; occlusion: NativeViewOcclusion; sessionId: string; visible: boolean; onActivate?: () => void;
}) {
  const { t } = useShellLanguage();
  const session = useSyncExternalStore(hub.subscribe, () => hub.session(sessionId));
  const supported = useSyncExternalStore(hub.subscribe, hub.supported);
  const pages = session?.pages ?? [];
  const front = pages.find(page => page.id === session?.activePageId) ?? null;
  const frontId = front?.id ?? null;
  const [draft, setDraft] = useState<Readonly<{ page: string | null; text: string }> | null>(null);
  const [notice, setNotice] = useState<MessageKey | null>(null);
  // Whether the view is hidden because something of the window is drawn over its place, and the picture shown then.
  const [covered, setCovered] = useState(false);
  const [still, setStill] = useState<Readonly<{ page: string } & BrowserStill> | null>(null);
  const placeholder = useRef<HTMLDivElement>(null);
  const addressField = useRef<HTMLInputElement>(null);
  const frontPage = useRef(frontId);
  frontPage.current = frontId;

  useEffect(() => { hub.ensure(sessionId); }, [hub, sessionId]);
  useEffect(() => { setDraft(null); setNotice(null); setStill(null); }, [sessionId]);
  // A browser without a tab waits for an address.
  useEffect(() => { if (visible && session && pages.length === 0) addressField.current?.focus(); }, [visible, !!session, pages.length]);

  // Where the browser tab in front is shown. The placeholder moves without changing its size (a splitter, the
  // Explorer), and an overlay moves without the document changing, so both are looked at once for each frame while
  // the tab is shown. The hub tells the host a place only when it changed.
  useEffect(() => {
    if (!visible || supported === false) return;
    let frame = 0;
    let placed: BrowserBox | null | undefined;
    let wasCovered = false;
    const look = () => {
      const element = placeholder.current;
      const rect = element && frontPage.current ? element.getBoundingClientRect() : null;
      const box = rect ? boxOf(rect) : null;
      const hidden = !!rect && !!box && occlusion.occludes({ left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom }, element);
      if (hidden !== wasCovered) {
        wasCovered = hidden;
        setCovered(hidden);
        // The picture is taken as the view is hidden: WebKit draws a hidden view too, in a few milliseconds.
        const page = frontPage.current;
        if (hidden && page) void hub.capture(sessionId, page).then(picture => { if (picture && frontPage.current === page) setStill({ page, ...picture }); });
      }
      const shown = hidden ? null : box;
      if (placed !== undefined && sameBox(placed, shown)) return;
      placed = shown;
      hub.place(sessionId, shown);
    };
    const loop = () => { look(); frame = requestAnimationFrame(loop); };
    const unsubscribe = occlusion.subscribe(look);
    loop();
    return () => {
      cancelAnimationFrame(frame);
      unsubscribe();
      hub.place(sessionId, null);
    };
  }, [hub, occlusion, sessionId, visible, supported]);

  const editing = draft && draft.page === frontId ? draft.text : null;
  const address = editing ?? shownAddress(front);

  async function go() {
    const text = (editing ?? "").trim();
    if (!text) return;
    const reply = front ? await hub.navigate(sessionId, front.id, text) : await hub.open(sessionId, text);
    setNotice(browserNotice(reply.status));
    if (reply.status !== "ok") return;
    setDraft(null);
    const page = front?.id ?? reply.pageId;
    // The page that was sent somewhere takes the keyboard, as in a browser.
    if (page) void hub.command(sessionId, page, "focus");
  }

  async function openTab() {
    const reply = await hub.open(sessionId, null);
    setNotice(browserNotice(reply.status));
    setDraft({ page: reply.pageId ?? null, text: "" });
    setTimeout(() => addressField.current?.focus(), 0);
  }

  async function closeTab(page: BrowserPageItem) {
    const reply = await hub.close(sessionId, page.id);
    setNotice(browserNotice(reply.status));
  }

  function onAddressKey(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === "Enter") { event.preventDefault(); void go(); }
    else if (event.key === "Escape") { event.preventDefault(); setDraft(null); event.currentTarget.select(); }
  }

  if (supported === false) {
    return <section className="browser-tab" onFocus={onActivate}>
      <NonIdealState icon={<AppIcon name="browse" size={32} />} title={t("Built-in browser")} description={t("This system cannot show a browser inside the window.")} />
    </section>;
  }

  return (
    <section className="browser-tab" aria-label={t("Browser")} onFocus={onActivate} onPointerDown={onActivate}>
      <div className="browser-strip" role="tablist" aria-label={t("Browser tabs")}>
        {pages.map(page => {
          const label = pageLabel(page, t("New tab"));
          const selected = page.id === frontId;
          return (
            <div key={page.id} className={`browser-strip-tab${selected ? " selected" : ""}`} title={shownAddress(page) || label}>
              <button type="button" role="tab" aria-selected={selected} className="browser-strip-select"
                onClick={() => { if (!selected) void hub.select(sessionId, page.id); }}
                onAuxClick={event => { if (event.button === 1) { event.preventDefault(); void closeTab(page); } }}>
                {page.loading ? <span className="browser-strip-loading" aria-hidden="true" /> : null}
                <span className="browser-strip-title">{label}</span>
              </button>
              <Button minimal small className="browser-strip-close" icon={<AppIcon name="close" size={12} />} aria-label={t("Close {title}", { title: label })} onClick={() => void closeTab(page)} />
            </div>
          );
        })}
        <Button minimal small className="browser-strip-new" icon={<AppIcon name="plus" size={14} />} aria-label={t("New tab")} title={t("New tab")} onClick={() => void openTab()} />
      </div>
      <div className="browser-toolbar">
        <Button minimal small icon={<AppIcon name="arrowLeft" size={16} />} aria-label={t("Back")} title={t("Back")} disabled={!front?.canGoBack}
          onClick={() => front && void hub.command(sessionId, front.id, "back")} />
        <Button minimal small icon={<AppIcon name="arrowRight" size={16} />} aria-label={t("Forward")} title={t("Forward")} disabled={!front?.canGoForward}
          onClick={() => front && void hub.command(sessionId, front.id, "forward")} />
        {front?.loading
          ? <Button minimal small icon={<AppIcon name="stop" size={14} />} aria-label={t("Stop")} title={t("Stop")} onClick={() => void hub.command(sessionId, front.id, "stop")} />
          : <Button minimal small icon={<AppIcon name="refresh" size={16} />} aria-label={t("Reload")} title={t("Reload")} disabled={!front}
            onClick={() => front && void hub.command(sessionId, front.id, "reload")} />}
        <input ref={addressField} className="browser-address bp6-input bp6-small" type="text" spellCheck={false} autoComplete="off"
          aria-label={t("Address")} placeholder={t("Type an address, such as localhost:5173")} value={address}
          onChange={event => setDraft({ page: frontId, text: event.target.value })} onKeyDown={onAddressKey}
          onFocus={event => event.currentTarget.select()} />
      </div>
      {notice && <div className="browser-notice" role="status">{t(notice)}</div>}
      <div ref={placeholder} className="browser-place">
        {covered && still && still.page === frontId ? <img className="browser-still" src={still.image} alt="" draggable={false} /> : null}
        {session && pages.length === 0
          ? <NonIdealState className="browser-empty" icon={<AppIcon name="browse" size={32} />} title={t("Built-in browser")}
            description={t("Type an address above, such as the one of your application on localhost.")} />
          : null}
      </div>
    </section>
  );
}
