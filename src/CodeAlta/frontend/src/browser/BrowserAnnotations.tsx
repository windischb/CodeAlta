import { Button } from "@blueprintjs/core";
import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import type { BrowserAnnotationItem, BrowserAnnotationsItem, BrowserElementItem } from "#neoastra";
import { AppIcon } from "../AppIcon";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import type { BrowserHub } from "./browserHub";

/** An element in a few words, as the page reported it: its tag, and its name or its text. */
export function elementSummary(element: BrowserElementItem): string {
  const said = element.name || element.text;
  return said ? `<${element.tag}> ${said}` : `<${element.tag}>`;
}

/** What a reply about annotations that is not `ok` says, in words; nothing for a reply that needs none. */
export function annotationNotice(status: string): MessageKey | null {
  switch (status) {
    case "ok": case "not_found": case "no_pick": return null;
    case "limit": return "This session has too many annotations. Delete some to add another.";
    case "busy": return "The page asks a question. Answer it first.";
    case "failed": return "The page did not answer. Try again.";
    case "invalid": return "Write what should change, in 4000 characters at most.";
    default: return "The browser is not available right now.";
  }
}

// The picture of an element, read from the host when it is first shown. `revision` changes with what is picked.
function ElementPicture({ hub, sessionId, number, revision = "" }: { hub: BrowserHub; sessionId: string; number: number; revision?: string }) {
  const { t } = useShellLanguage();
  const [image, setImage] = useState<string | null>(null);
  useEffect(() => {
    let current = true;
    setImage(null);
    void hub.annotationImage(sessionId, number).then(picture => { if (current) setImage(picture); });
    return () => { current = false; };
  }, [hub, sessionId, number, revision]);
  return image ? <img className="browser-annotation-picture" src={image} alt={t("Picture of the element")} /> : null;
}

/**
 * The annotations of a session, under the place of its page: the element that was just picked, with a field
 * for what should change, and the list of what was annotated, the open ones first. What describes an element
 * is text of the page; the text of an annotation is typed here and nowhere else.
 */
export function BrowserAnnotations({ hub, sessionId, annotations, onNotice, onAddToPrompt, onClose }: {
  hub: BrowserHub; sessionId: string; annotations: BrowserAnnotationsItem | null;
  onNotice: (status: string) => void; onAddToPrompt: () => void; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const [text, setText] = useState("");
  const [saving, setSaving] = useState(false);
  const field = useRef<HTMLTextAreaElement>(null);
  const pending = annotations?.pending ?? null;
  const list = annotations?.annotations ?? [];
  // What was picked, as far as it tells one pick from the next.
  const picked = pending ? `${pending.pageId} ${pending.element.selector}` : "";
  useEffect(() => { if (picked) { setText(""); field.current?.focus(); } }, [picked]);

  async function save() {
    if (!text.trim() || saving) return;
    setSaving(true);
    const reply = await hub.saveAnnotation(sessionId, text);
    setSaving(false);
    onNotice(reply.status);
    if (reply.status === "ok") setText("");
  }
  const discard = () => void hub.discardPick(sessionId).then(reply => onNotice(reply.status));
  const change = (number: number, action: "resolve" | "reopen" | "delete" | "reveal") => void hub.changeAnnotation(sessionId, number, action).then(reply => onNotice(reply.status));
  function fieldKey(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (event.nativeEvent.isComposing) return;
    if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) { event.preventDefault(); void save(); }
    else if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); discard(); }
  }

  const element = (item: BrowserElementItem) => <span className="browser-annotation-element" title={`${t("Element as the page reports it")}\n${item.selector}\n${item.html}`}>{elementSummary(item)}</span>;
  const row = (item: BrowserAnnotationItem) => <li key={item.number} className="browser-annotation" data-status={item.status}>
    <span className="browser-annotation-number" aria-label={t("Annotation {number}", { number: item.number })}>{item.number}</span>
    <div className="browser-annotation-body">
      <p className="browser-annotation-text">{item.text}</p>
      {element(item.element)}
      <span className="browser-annotation-page" title={item.address}>{item.title || item.address}</span>
      {item.found === false && <span className="browser-annotation-missing">{t("Not found on this page")}</span>}
      {item.status === "resolved" && <span className="browser-annotation-resolved">{t(item.resolvedBy === "agent" ? "Resolved by the agent" : "Resolved")}{item.note ? `: ${item.note}` : ""}</span>}
      {item.hasImage && <ElementPicture hub={hub} sessionId={sessionId} number={item.number} />}
    </div>
    <div className="browser-annotation-actions">
      <Button variant="minimal" size="small" icon={<AppIcon name="goto" size={14} />} aria-label={t("Go to the element")} title={t("Go to the element")} onClick={() => change(item.number, "reveal")} />
      {item.status === "resolved"
        ? <Button variant="minimal" size="small" icon={<AppIcon name="restore" size={14} />} aria-label={t("Reopen")} title={t("Reopen")} onClick={() => change(item.number, "reopen")} />
        : <Button variant="minimal" size="small" icon={<AppIcon name="check" size={14} />} aria-label={t("Resolve")} title={t("Resolve")} onClick={() => change(item.number, "resolve")} />}
      <Button variant="minimal" size="small" icon={<AppIcon name="trash" size={14} />} aria-label={t("Delete")} title={t("Delete")} onClick={() => change(item.number, "delete")} />
    </div>
  </li>;

  return <section className="browser-annotations" aria-label={t("Annotations")}>
    <header className="browser-annotations-bar">
      <strong>{t("Annotations")}</strong>
      <span className="browser-annotations-count">{t("{count} open", { count: annotations?.open ?? 0 })}</span>
      <span className="browser-bar-space" />
      <Button size="small" icon={<AppIcon name="prompt" size={14} />} text={t("Add to prompt")} title={t("Put the open annotations into the prompt of the session")}
        disabled={!annotations?.open} onClick={onAddToPrompt} />
      <Button variant="minimal" size="small" icon={<AppIcon name="chevronDown" size={15} />} aria-label={t("Hide the annotations")} title={t("Hide the annotations")} onClick={onClose} />
    </header>
    <div className="browser-annotations-body">
      {pending && <form className="browser-annotation-new" onSubmit={event => { event.preventDefault(); void save(); }}>
        <div className="browser-annotation-picked">
          {pending.hasImage && <ElementPicture hub={hub} sessionId={sessionId} number={0} revision={picked} />}
          <div>{element(pending.element)}<code>{pending.element.selector}</code></div>
        </div>
        <textarea ref={field} rows={2} maxLength={4000} aria-label={t("What should change?")} placeholder={t("What should change?")} value={text}
          onChange={event => setText(event.target.value)} onKeyDown={fieldKey} />
        <div className="browser-annotation-buttons">
          <Button type="button" size="small" text={t("Cancel")} onClick={discard} />
          <Button type="submit" size="small" intent="primary" text={t("Save annotation")} disabled={!text.trim() || saving} />
        </div>
      </form>}
      {list.length > 0
        ? <ol className="browser-annotation-list">{list.map(row)}</ol>
        : !pending && <p className="browser-annotations-empty">{t("No annotations yet. Choose Annotate and click an element of the page.")}</p>}
    </div>
  </section>;
}
