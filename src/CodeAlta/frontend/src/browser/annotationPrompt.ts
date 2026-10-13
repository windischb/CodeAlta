// Puts the open annotations of a session into the composer of that session: the text the host writes for them,
// and the pictures of their elements as prompt images. Nothing is sent: the user completes the prompt and sends it.
import { askPluginComposer } from "../pluginUi";
import type { BrowserHub } from "./browserHub";

/**
 * What became of the annotations: `ok` with the pictures the composer took, `text` when only the text is in
 * the prompt, `none` when there is no open annotation, `no_composer` when the session has no prompt that can
 * be written into, `failed` when the host did not answer.
 */
export type AnnotationPromptOutcome = "ok" | "text" | "none" | "no_composer" | "failed";

/** A picture as a file, from its `data:` address; null for anything else. */
export function pictureFile(address: string, name: string): File | null {
  const match = /^data:(image\/(?:png|jpeg));base64,([A-Za-z0-9+/=]+)$/.exec(address);
  if (!match) return null;
  try {
    const bytes = Uint8Array.from(atob(match[2]), character => character.charCodeAt(0));
    return new File([bytes], `${name}.${match[1] === "image/png" ? "png" : "jpg"}`, { type: match[1] });
  } catch { return null; }
}

/** The draft of a prompt with the annotations after what it holds already. */
export function withAnnotations(draft: string, text: string): string {
  return draft.trim() ? `${draft.trimEnd()}\n\n${text}` : text;
}

/**
 * Adds the open annotations to the prompt of a session. The pictures go the way pasted images go: a paste
 * event on the editor of the composer, which decides as for any paste whether its model takes images, and says
 * so itself when it does not. `row` is the pane of the session, which holds its composer.
 */
export async function addAnnotationsToPrompt(hub: BrowserHub, sessionId: string, row: Element | null): Promise<AnnotationPromptOutcome> {
  const prompt = await hub.annotationPrompt(sessionId);
  if (prompt.status === "not_found") return "none";
  if (prompt.status !== "ok" || !prompt.text) return "failed";
  const composer = askPluginComposer("state", sessionId).state;
  if (!composer || !askPluginComposer("draft", sessionId, withAnnotations(composer.draftText, prompt.text)).result) return "no_composer";
  if (prompt.pictures.length === 0) return "ok";
  const files: File[] = [];
  for (const number of prompt.pictures) {
    const picture = await hub.annotationImage(sessionId, number);
    const file = picture ? pictureFile(picture, `annotation-${number}`) : null;
    if (file) files.push(file);
  }
  // The composer drew the new text meanwhile: its editor is the one that takes the paste.
  const editor = row?.querySelector<HTMLElement>('.session-workspace [id^="session-prompt"]');
  if (files.length === 0 || !editor || typeof DataTransfer === "undefined") return "text";
  const transfer = new DataTransfer();
  for (const file of files) transfer.items.add(file);
  const paste = new ClipboardEvent("paste", { clipboardData: transfer, bubbles: true, cancelable: true });
  editor.dispatchEvent(paste);
  // A composer that takes pasted files says so by keeping the paste from the editor.
  return paste.defaultPrevented ? "ok" : "text";
}
