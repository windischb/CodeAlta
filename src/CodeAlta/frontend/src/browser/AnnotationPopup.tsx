import { Button } from "@blueprintjs/core";
import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from "react";
import type { BrowserAnnotationItem, BrowserElementItem, BrowserElementPlaceItem } from "#neoastra";
import { useShellLanguage } from "../shellLanguage";
import { elementSummary } from "./BrowserAnnotations";
import type { BrowserHub } from "./browserHub";
import { elementRect, popupPosition, type PopupRect, type PopupSize } from "./annotationPopupPlacement";

/** What the popup shows: the element that was just picked, or an annotation that was opened. */
export type AnnotationPopupSubject = Readonly<{ kind: "pick"; element: BrowserElementItem } | { kind: "open"; annotation: BrowserAnnotationItem }>;

/**
 * The popup of an annotation, at its element, in the place of the page. The view of the page is hidden while it is
 * open, and the place shows a still picture of the page under it: the text of an annotation is typed here, in the
 * window, never in the page. For an element that was picked it holds the field and saves; for an annotation that
 * was opened it shows the text, and resolves, reopens or deletes it.
 */
export function AnnotationPopup({ hub, sessionId, subject, place, onNotice, onClose }: {
  hub: BrowserHub; sessionId: string; subject: AnnotationPopupSubject; place: BrowserElementPlaceItem | null;
  onNotice: (status: string) => void; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const popup = useRef<HTMLFormElement>(null);
  const field = useRef<HTMLTextAreaElement>(null);
  const [text, setText] = useState("");
  const [saving, setSaving] = useState(false);
  // The size of the place of the page and of the popup, measured once drawn and again when the place changes.
  const [area, setArea] = useState<PopupSize | null>(null);
  const [size, setSize] = useState<PopupSize | null>(null);
  useLayoutEffect(() => {
    const element = popup.current, parent = element?.parentElement;
    if (!element || !parent) return;
    const measure = () => {
      setArea(current => current && current.width === parent.clientWidth && current.height === parent.clientHeight ? current : { width: parent.clientWidth, height: parent.clientHeight });
      setSize(current => current && current.width === element.offsetWidth && current.height === element.offsetHeight ? current : { width: element.offsetWidth, height: element.offsetHeight });
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(parent);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  const target: PopupRect | null = place && area ? elementRect(place, area) : null;
  const at = area && size ? popupPosition(target, size, area) : null;
  // Once it is placed, and can be focused, the keyboard goes to the popup: to its field for a pick, to the popup
  // itself for an annotation.
  const shown = !!at;
  useEffect(() => { if (shown) (subject.kind === "pick" ? field.current : popup.current)?.focus(); }, [shown, subject.kind]);

  async function save() {
    if (subject.kind !== "pick" || !text.trim() || saving) return;
    setSaving(true);
    const reply = await hub.saveAnnotation(sessionId, text);
    setSaving(false);
    onNotice(reply.status);
  }
  const discard = () => void hub.discardPick(sessionId).then(reply => onNotice(reply.status));
  const change = (number: number, action: "resolve" | "reopen" | "delete") => void hub.changeAnnotation(sessionId, number, action).then(reply => {
    onNotice(reply.status);
    if (action === "delete" && reply.status === "ok") onClose();
  });
  function key(event: KeyboardEvent<HTMLFormElement>) {
    if (event.nativeEvent.isComposing) return;
    if (event.key === "Escape") {
      event.preventDefault(); event.stopPropagation();
      if (subject.kind === "pick") discard(); else onClose();
    } else if (event.key === "Enter" && (event.ctrlKey || event.metaKey) && subject.kind === "pick") { event.preventDefault(); void save(); }
  }

  const element = subject.kind === "pick" ? subject.element : subject.annotation.element;
  const annotation = subject.kind === "open" ? subject.annotation : null;
  return <>
    {target && <div className="browser-popup-target" aria-hidden style={{ left: target.left, top: target.top, width: target.width, height: target.height }} />}
    <form ref={popup} className="browser-popup" role="dialog" tabIndex={-1} data-kind={subject.kind}
      aria-label={annotation ? t("Annotation {number}", { number: annotation.number }) : t("What should change?")}
      style={at ? { left: at.left, top: at.top } : { visibility: "hidden" }}
      onKeyDown={key} onSubmit={event => { event.preventDefault(); void save(); }}>
      <header className="browser-popup-head">
        {annotation && <span className="browser-annotation-number">{annotation.number}</span>}
        <span className="browser-annotation-element" title={`${t("Element as the page reports it")}\n${element.selector}\n${element.html}`}>{elementSummary(element)}</span>
      </header>
      {annotation
        ? <>
          <p className="browser-popup-text">{annotation.text}</p>
          {!place && <span className="browser-annotation-missing">{t("Not found on this page")}</span>}
          {annotation.status === "resolved" && <span className="browser-annotation-resolved">{t(annotation.resolvedBy === "agent" ? "Resolved by the agent" : "Resolved")}{annotation.note ? `: ${annotation.note}` : ""}</span>}
          <div className="browser-annotation-buttons">
            <Button type="button" size="small" text={t("Delete")} onClick={() => change(annotation.number, "delete")} />
            {annotation.status === "resolved"
              ? <Button type="button" size="small" text={t("Reopen")} onClick={() => change(annotation.number, "reopen")} />
              : <Button type="button" size="small" text={t("Resolve")} onClick={() => change(annotation.number, "resolve")} />}
            <Button type="button" size="small" intent="primary" text={t("Close")} onClick={onClose} />
          </div>
        </>
        : <>
          <textarea ref={field} rows={3} maxLength={4000} aria-label={t("What should change?")} placeholder={t("What should change?")} value={text}
            onChange={event => setText(event.target.value)} />
          <div className="browser-annotation-buttons">
            <Button type="button" size="small" text={t("Cancel")} onClick={discard} />
            <Button type="submit" size="small" intent="primary" text={t("Save annotation")} disabled={!text.trim() || saving} />
          </div>
        </>}
    </form>
  </>;
}
