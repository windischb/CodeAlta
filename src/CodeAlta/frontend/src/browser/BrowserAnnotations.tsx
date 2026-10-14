import { Button } from "@blueprintjs/core";
import { useEffect, useState } from "react";
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

// The picture of an element, read from the host when it is first shown.
function ElementPicture({ hub, sessionId, number }: { hub: BrowserHub; sessionId: string; number: number }) {
  const { t } = useShellLanguage();
  const [image, setImage] = useState<string | null>(null);
  useEffect(() => {
    let current = true;
    setImage(null);
    void hub.annotationImage(sessionId, number).then(picture => { if (current) setImage(picture); });
    return () => { current = false; };
  }, [hub, sessionId, number]);
  return image ? <img className="browser-annotation-picture" src={image} alt={t("Picture of the element")} /> : null;
}

/**
 * The annotations of a session, under the place of its page: the list of what was annotated, the open ones first.
 * What describes an element is text of the page. The text of an annotation is typed in the popup at its element
 * (`AnnotationPopup`), which the text of an annotation in this list opens.
 */
export function BrowserAnnotations({ hub, sessionId, annotations, onNotice, onAddToPrompt, onClose }: {
  hub: BrowserHub; sessionId: string; annotations: BrowserAnnotationsItem | null;
  onNotice: (status: string) => void; onAddToPrompt: () => void; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const list = annotations?.annotations ?? [];
  const change = (number: number, action: "resolve" | "reopen" | "delete" | "reveal" | "open") => void hub.changeAnnotation(sessionId, number, action).then(reply => onNotice(reply.status));

  const element = (item: BrowserElementItem) => <span className="browser-annotation-element" title={`${t("Element as the page reports it")}\n${item.selector}\n${item.html}`}>{elementSummary(item)}</span>;
  const row = (item: BrowserAnnotationItem) => <li key={item.number} className="browser-annotation" data-status={item.status}>
    <span className="browser-annotation-number" aria-label={t("Annotation {number}", { number: item.number })}>{item.number}</span>
    <div className="browser-annotation-body">
      <button type="button" className="browser-annotation-text" title={t("Open at the element")} onClick={() => change(item.number, "open")}>{item.text}</button>
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
      {list.length > 0
        ? <ol className="browser-annotation-list">{list.map(row)}</ol>
        : <p className="browser-annotations-empty">{t("No annotations yet. Choose Annotate and click an element of the page.")}</p>}
    </div>
  </section>;
}
