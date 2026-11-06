import { Button } from "@blueprintjs/core";
import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from "react";
import type { BrowserDialogItem } from "#neoastra";
import { useShellLanguage } from "../shellLanguage";

/** Who asks, as browsers name it: the host of the address of a page, or the address when it has none. */
export function dialogOrigin(address: string): string {
  try { return new URL(address).host || address; } catch { return address; }
}

/**
 * A JavaScript dialog of a page (`alert`, `confirm`, `prompt`, or the question before a document is left), in
 * the place of the page: the page shows none by itself, and waits until it is answered here or by a tool of the
 * session. The message and the default text are text of the page; the answer is the user's. Enter accepts and
 * Escape dismisses.
 */
export function BrowserDialog({ dialog, address, onAnswer }: {
  dialog: BrowserDialogItem; address: string; onAnswer: (accept: boolean, text: string | null) => Promise<boolean>;
}) {
  const { t } = useShellLanguage();
  const [text, setText] = useState(dialog.defaultText ?? "");
  const [sent, setSent] = useState(false);
  const form = useRef<HTMLFormElement>(null);
  const field = useRef<HTMLInputElement>(null);
  const accept = useRef<HTMLButtonElement>(null);
  const prompt = dialog.kind === "prompt", leaving = dialog.kind === "beforeunload";

  // The keyboard comes to the dialog when it was with the page that asks (the window then has none), or with the
  // browser tab: what is typed elsewhere in the window is left alone.
  useEffect(() => {
    const holder = document.activeElement;
    const tab = form.current?.closest(".browser-tab");
    if (document.hasFocus() && holder && holder !== document.body && !tab?.contains(holder)) return;
    const target = prompt ? field.current : accept.current;
    target?.focus();
    if (prompt) field.current?.select();
  }, [prompt]);

  // An answer is given once, whatever gives it: a key, a button, or the form.
  const given = useRef(false);
  const answer = (accepted: boolean) => {
    if (given.current) return;
    given.current = true;
    setSent(true);
    // An answer the host did not take can be given again.
    void onAnswer(accepted, accepted && prompt ? text : null).then(taken => { if (!taken) { given.current = false; setSent(false); } });
  };
  const submit = (event: FormEvent) => { event.preventDefault(); answer(true); };
  const key = (event: KeyboardEvent) => {
    if (event.nativeEvent.isComposing) return;
    // Enter in the field accepts; on a button it presses that button.
    if (event.key === "Enter" && event.target === field.current) { event.preventDefault(); answer(true); }
    else if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); answer(false); }
  };

  return <form ref={form} className="browser-dialog" role="alertdialog" aria-labelledby="browser-dialog-origin" aria-describedby="browser-dialog-message" onSubmit={submit} onKeyDown={key}>
    <strong id="browser-dialog-origin">{t("{origin} says", { origin: dialogOrigin(address) })}</strong>
    <p id="browser-dialog-message">{leaving && !dialog.message ? t("Leave this page? Changes you made may not be saved.") : dialog.message}</p>
    {prompt && <input ref={field} className="browser-dialog-text bp6-input bp6-small" type="text" spellCheck={false} autoComplete="off" maxLength={4000}
      aria-label={t("Answer for the page")} value={text} onChange={event => setText(event.target.value)} />}
    <div className="browser-dialog-buttons">
      {dialog.kind !== "alert" && <Button type="button" small text={t(leaving ? "Stay" : "Cancel")} disabled={sent} onClick={() => answer(false)} />}
      <Button ref={accept} type="submit" small intent="primary" text={t(leaving ? "Leave" : "OK")} disabled={sent} />
    </div>
  </form>;
}
