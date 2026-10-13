import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "../browserTarget";

test("the browser of a session says where its page is, hides it behind what the window shows over it, and drives its pages", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-browser-pane-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./browserPane.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      plugins: [{ name: "isolated-browser", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("../demo-api.ts", import.meta.url)) }));
      } }] });
    await writeFile(join(root, "style.css"), await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("./browser.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body style="margin:0"><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown }; exceptionDetails?: unknown }>((resolve, reject) => {
      const id = ++sequence, timer = setTimeout(() => reject(Error(`${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)); if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(`${method}: ${JSON.stringify(message.error)}`)); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result?.value;
    };
    const act = (script: string) => evaluate(`(async () => { ${script}; for (let turn = 0; turn < 4; turn++) await new Promise(resolve => requestAnimationFrame(() => setTimeout(resolve, 0))); return true; })()`);
    const until = async (expression: string) => assert.equal(await evaluate(`new Promise(resolve => { const end = Date.now() + 7000; (function check() {
      if (${expression}) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`), true, expression);
    const key = async (value: string, code: number) => { await command("Input.dispatchKeyEvent", { type: "keyDown", key: value, code: value, windowsVirtualKeyCode: code }); await command("Input.dispatchKeyEvent", { type: "keyUp", key: value, code: value, windowsVirtualKeyCode: code }); await act(""); };
    const f = "window.browserFixture", state = `${f}.state`, view = "document.querySelector('.browser-view')", address = "document.querySelector('.browser-address')";
    const last = `${state}.places.at(-1)`;
    // The rectangle of the placeholder on the display: its edges in CSS pixels, scaled and rounded.
    const expected = `(() => { const box = ${view}.getBoundingClientRect(), scale = window.devicePixelRatio, left = Math.round(box.left * scale), top = Math.round(box.top * scale);
      return { x: left, y: top, width: Math.round(box.right * scale) - left, height: Math.round(box.bottom * scale) - top }; })()`;
    const placed = async (why: string) => { await until(`${view} && JSON.stringify(${last}) === JSON.stringify(${expected})`); assert.deepEqual(await evaluate(last), await evaluate(expected), why); };
    const hidden = async (why: string) => { await until(`${state}.places.length > 0 && ${last} === null`); assert.equal(await evaluate(last), null, why); };
    const type = (text: string) => act(`${address}.focus(); Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(${address}, ${JSON.stringify(text)}); ${address}.dispatchEvent(new Event('input', { bubbles: true }))`);
    const calls = async () => await evaluate(`${state}.calls.splice(0)`) as string[];
    const button = (label: string) => `document.querySelector('.browser-pane button[aria-label="${label}"]')`;
    const modal = "const dialog = document.createElement('dialog'); dialog.id = 'modal'; dialog.textContent = 'Settings'; document.body.append(dialog); dialog.showModal()";

    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1200, height: 700, deviceScaleFactor: 1.5, mobile: false });
    // A page that is not in front gets no focus event: this one is told it is.
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await until(`window.browserFixture && ${view} && ${state}.places.length > 0`);

    // The placeholder is reported in the pixels of the display, not in CSS pixels.
    assert.equal(await evaluate("window.devicePixelRatio"), 1.5);
    await placed("the placeholder of the page in front");
    const first = await evaluate(last) as { x: number; y: number; width: number; height: number };
    const css = await evaluate(`(() => { const box = ${view}.getBoundingClientRect(); return { left: box.left, top: box.top, width: box.width, height: box.height }; })()`) as { left: number; top: number; width: number; height: number };
    assert.ok(css.width > 300 && css.height > 300, JSON.stringify(css));
    assert.ok(Math.abs(first.width - css.width * 1.5) <= 1 && Math.abs(first.x - css.left * 1.5) <= 1, JSON.stringify([first, css]));
    assert.ok((await calls()).includes("ensure fixture"));
    // The frame: a tab list of the pages, the page in front marked, and every button named.
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.browser-tabs [role=tab]')].map(tab => [tab.textContent, tab.getAttribute('aria-selected')])"), [["Page a", "true"], ["Page b", "false"]]);
    assert.equal(await evaluate("document.querySelector('.browser-tabs').getAttribute('role')"), "tablist");
    assert.equal(await evaluate("[...document.querySelectorAll('.browser-pane button')].every(button => !!button.getAttribute('aria-label') || button.getAttribute('role') === 'tab')"), true);
    assert.equal(await evaluate("[...document.querySelectorAll('.browser-pane button:not([role=tab])')].every(button => !!button.title)"), true);
    assert.equal(await evaluate(`${address}.value`), "https://example.com/a");
    assert.equal(await evaluate(`${address}.getAttribute('aria-label')`), "Address of the page");
    assert.equal(await evaluate("document.querySelector('.browser-pane').scrollWidth <= document.querySelector('.browser-pane').clientWidth"), true);
    // The splitter's hit area ends where the browser starts.
    assert.ok(await evaluate("(() => { const hit = getComputedStyle(document.querySelector('.browser-splitter'), '::before'); return hit.right === '0px' && parseFloat(hit.left) < 0; })()"));

    // The placeholder moves without changing its size: the next frame says where it is.
    const places = await evaluate(`${state}.places.length`) as number;
    await act(`${f}.setSize({ width: 1000, height: 600 })`);
    await placed("after the pane moved");
    assert.ok(await evaluate(`${state}.places.length`) as number > places);
    assert.notDeepEqual(await evaluate(last), first);
    // Nothing moves: nothing more is said.
    const quiet = await evaluate(`${state}.places.length`);
    await act(""); await act("");
    assert.equal(await evaluate(`${state}.places.length`), quiet);

    // Another session in front, another tab of the window, Settings: the view is hidden.
    await act(`${f}.setShown(false)`);
    await hidden("a pane that is not shown");
    await act(`${f}.setShown(true)`);
    await placed("shown again");

    // A modal window covers the page wherever its box is: the view is hidden while it is open.
    await act(modal);
    await hidden("behind a modal window");
    // Without a picture of the page, its place says what the page is.
    assert.deepEqual(await evaluate("[document.querySelector('.browser-covered strong')?.textContent, document.querySelector('.browser-covered span')?.textContent]"), ["Page a", "https://example.com/a"]);
    await act("document.getElementById('modal').close()");
    await placed("the modal window closed");
    assert.equal(await evaluate("document.querySelector('.browser-covered')"), null);
    await act("document.getElementById('modal').remove()");

    // A menu covers the view only where it is: beside the view it leaves it, over it it hides it.
    await act("const menu = document.createElement('div'); menu.id = 'menu'; menu.className = 'bp6-popover'; menu.style.cssText = 'position:fixed;left:20px;top:80px;width:160px;height:200px'; document.body.append(menu)");
    await act("");
    assert.notEqual(await evaluate(last), null, "a menu beside the view");
    // It moves without the document changing.
    await act(`document.getElementById('menu').style.left = (${view}.getBoundingClientRect().left - 40) + 'px'`);
    await hidden("a menu over the edge of the view");
    await act("document.getElementById('menu').remove()");
    await placed("the menu closed");

    // A toast of the window is beside the browser, as wide as the room it has there: it does not hide the view.
    const toast = (name: string) => `const host = document.createElement('div'); host.id = 'toasts'; host.className = 'bp6-toast-container bp6-toast-container-in-portal bp6-toast-container-${name === "app-toaster" ? "top" : "bottom"} bp6-toast-container-right ${name}';
      host.innerHTML = '<div class="bp6-toast"><span class="bp6-toast-message">CodeAlta 9.9.9 is available, and this notice stays until it is closed.</span></div>'; document.body.append(host)`;
    const beside = `(() => { const box = document.querySelector('#toasts .bp6-toast').getBoundingClientRect(), pane = document.querySelector('.browser-pane').getBoundingClientRect();
      return box.width >= 300 && box.left >= 0 && box.right <= pane.left; })()`;
    for (const name of ["app-toaster", "app-update-toaster"]) {
      await act(toast(name));
      await act("");
      assert.ok(await evaluate(beside), `${name}: beside the browser`);
      await placed(`${name}: a toast beside the browser leaves the view shown`);
      assert.equal(await evaluate("document.querySelector('.browser-covered, .browser-still')"), null);
      await act("document.getElementById('toasts').remove()");
    }
    // The pane goes: the toasts are back at the edge of the window.
    await act(toast("app-toaster"));
    await act(`${f}.setShown(false)`);
    await hidden("a pane that is not shown, with a toast");
    assert.ok(await evaluate("Math.abs(document.querySelector('#toasts .bp6-toast').getBoundingClientRect().right - (document.documentElement.clientWidth - 20)) < 1"));
    await act(`${f}.setShown(true)`);
    await until(beside);
    await placed("shown again, with the toast beside it");
    // A window too narrow for a toast beside the browser: the toast is where it is by itself, and hides the view it crosses.
    await command("Emulation.setDeviceMetricsOverride", { width: 640, height: 700, deviceScaleFactor: 1.5, mobile: false });
    await act(`${f}.setSize({ width: 600, height: 600 })`);
    await hidden("a toast over the view of a narrow window");
    assert.ok(await evaluate("(() => { const box = document.querySelector('#toasts .bp6-toast').getBoundingClientRect(); return box.width >= 300 && box.left >= 0 && box.right <= window.innerWidth; })()"), "the toast is whole in a narrow window");
    await act("document.getElementById('toasts').remove()");
    await placed("the toast closed");
    await command("Emulation.setDeviceMetricsOverride", { width: 1200, height: 700, deviceScaleFactor: 1.5, mobile: false });
    await act(`${f}.setSize({ width: 1000, height: 600 })`);
    await placed("the window as it was");

    // The pointer comes back from the view: a picture of the page is taken, and drawn while the view is hidden.
    // The pictures taken after the pane was shown are over by now, and none was given.
    await act(`${state}.still = 'data:image/gif;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw=='; await new Promise(resolve => setTimeout(resolve, 1400))`);
    await calls();
    await act("document.dispatchEvent(new PointerEvent('pointerout', { bubbles: true, relatedTarget: null })); document.body.dispatchEvent(new PointerEvent('pointerover', { bubbles: true }))");
    await until(`${state}.calls.includes('capture fixture a')`);
    await act("document.body.dispatchEvent(new PointerEvent('pointerover', { bubbles: true }))");
    assert.equal((await calls()).filter(call => call.startsWith("capture")).length, 1, "a pointer that stays takes no other picture");
    assert.equal(await evaluate("document.querySelector('.browser-still')"), null, "nothing is drawn under the view");
    await act(modal);
    await hidden("behind a modal window again");
    assert.ok(await evaluate(`document.querySelector('.browser-still')?.getAttribute('src') === ${state}.still`));
    assert.equal(await evaluate("document.querySelector('.browser-covered')"), null);
    await act("document.getElementById('modal').close(); document.getElementById('modal').remove()");
    await placed("and shown again");

    // The address field: Enter goes where was typed and gives the keyboard to the page; Escape gives the address back.
    await calls();
    await act(`${address}.focus()`);
    assert.deepEqual(await evaluate(`[${address}.selectionStart, ${address}.selectionEnd]`), [0, "https://example.com/a".length], "focus selects the address");
    await type("  example.org/docs ");
    await key("Enter", 13);
    await until(`${state}.calls.includes('command fixture a focus')`);
    assert.deepEqual((await calls()).filter(call => !call.startsWith("capture")), ["navigate fixture a example.org/docs", "command fixture a focus"]);
    await type("something else");
    assert.equal(await evaluate(`${address}.value`), "something else");
    await key("Escape", 27);
    assert.equal(await evaluate(`${address}.value`), "https://example.com/a");
    await type("   "); await key("Enter", 13);
    assert.deepEqual((await calls()).filter(call => !call.startsWith("capture")), [], "nothing typed goes nowhere");
    await key("Escape", 27);

    // A refusal is said in words under the toolbar, and gone with the next request that is taken.
    for (const [status, text] of [["invalid", "This address cannot be opened. The browser opens http and https addresses."], ["limit", "This session has too many pages. Close one to open another."], ["failed", "The page could not be shown. Try again."]]) {
      await act(`${state}.status = '${status}'`);
      await type("ftp://example.org"); await key("Enter", 13);
      await until(`document.querySelector('.browser-notice')?.textContent === ${JSON.stringify(text)}`);
    }
    assert.ok(!(await calls()).some(call => call.startsWith("command")), "a page that was not loaded does not take the keyboard");
    await act(`${state}.status = 'ok'`);
    await type("example.org"); await key("Enter", 13);
    await until("document.querySelector('.browser-notice') === null");
    await until(`${state}.calls.includes('command fixture a focus')`);
    await calls();

    // The toolbar and the strip ask the host; the page in front follows what the host says.
    const asked = async () => (await calls()).filter(call => !call.startsWith("capture"));
    assert.deepEqual(await evaluate(`[${button("Go back")}.disabled, ${button("Go forward")}.disabled, ${button("Reload")}.disabled]`), [false, true, false]);
    await act(`${button("Go back")}.click(); ${button("Reload")}.click(); ${button("Open developer tools")}.click()`);
    assert.deepEqual(await asked(), ["command fixture a back", "command fixture a reload", "command fixture a devtools"]);
    await act(`${f}.setSession({ sessionId: 'fixture', activePageId: 'a', pages: [${f}.page('a', { loading: true, title: '' }), ${f}.page('b')] })`);
    assert.equal(await evaluate(`${button("Reload")}`), null);
    await act(`${button("Stop loading")}.click()`);
    assert.deepEqual(await asked(), ["command fixture a stop"]);
    assert.ok(await evaluate("document.querySelector('.browser-tab[data-front=true] .activity-spinner') !== null"), "a page that loads is marked");
    assert.equal(await evaluate("document.querySelector('.browser-tabs [role=tab]').textContent"), "https://example.com/a", "a page without a title shows its address");
    await act("document.querySelectorAll('.browser-tabs [role=tab]')[1].click()");
    assert.deepEqual(await asked(), ["select fixture b"]);
    await act(`${f}.setSession({ sessionId: 'fixture', activePageId: 'b', pages: [${f}.page('a'), ${f}.page('b')] })`);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.browser-tabs [role=tab]')].map(tab => tab.getAttribute('aria-selected'))"), ["false", "true"]);
    assert.equal(await evaluate(`${address}.value`), "https://example.com/b");
    await act("document.querySelectorAll('.browser-tab-close')[0].click()");
    await act("document.querySelectorAll('.browser-tab')[1].dispatchEvent(new MouseEvent('auxclick', { bubbles: true, button: 1 }))");
    assert.deepEqual(await asked(), ["close fixture a", "close fixture b"], "the close button of a page, and a middle click on it");
    // The arrows go from page to page in the strip.
    await act("document.querySelectorAll('.browser-tabs [role=tab]')[1].focus()");
    await key("ArrowLeft", 37);
    assert.deepEqual(await asked(), ["select fixture a"]);
    assert.equal(await evaluate("document.activeElement === document.querySelectorAll('.browser-tabs [role=tab]')[0]"), true);
    await act(`${button("New page")}.click()`);
    assert.deepEqual(await asked(), ["open fixture "]);
    assert.equal(await evaluate(`document.activeElement === ${address}`), true, "a new page waits for its address");
    await act(`${button("Close the browser")}.click()`);
    assert.equal(await evaluate(`${f}.closed`), 1);

    // A page that waits in a JavaScript dialog shows none by itself: its place shows the dialog, and the view is hidden.
    const dialog = "document.querySelector('.browser-dialog')";
    const asks = (id: string, value: string) => `${f}.setSession({ sessionId: 'fixture', activePageId: 'a', pages: [${f}.page('a', ${id === "a" ? `{ dialog: ${value} }` : "{}"}), ${f}.page('b', ${id === "b" ? `{ dialog: ${value} }` : "{}"})] })`;
    await act(`${f}.setSession({ sessionId: 'fixture', activePageId: 'a', pages: [${f}.page('a'), ${f}.page('b')] })`);
    await placed("before a page asks");
    await calls();
    await act(`${address}.focus(); ${asks("a", "{ kind: 'confirm', message: 'Delete the draft?\\nIt cannot be restored.', defaultText: null }")}`);
    await hidden("a page that waits in a dialog");
    assert.deepEqual(await evaluate(`[${dialog}.getAttribute('role'), ${dialog}.querySelector('strong').textContent, ${dialog}.querySelector('p').textContent, [...${dialog}.querySelectorAll('button')].map(button => button.textContent)]`),
      ["alertdialog", "example.com says", "Delete the draft?\nIt cannot be restored.", ["Cancel", "OK"]]);
    assert.equal(await evaluate(`${dialog}.querySelector('input')`), null);
    assert.ok(await evaluate(`document.activeElement === ${dialog}.querySelector('button[type=submit]')`), "the keyboard comes to the dialog from the browser part");
    assert.ok(await evaluate(`(() => { const box = ${dialog}.getBoundingClientRect(), place = ${view}.getBoundingClientRect(); return box.left >= place.left && box.right <= place.right && box.top >= place.top; })()`));
    assert.equal(await evaluate("document.querySelector('.browser-tab-mark')"), null, "the page in front shows its dialog, not a mark");
    // Escape dismisses, and the page takes the keyboard back once its view is shown again.
    await key("Escape", 27);
    await until(`${state}.calls.includes('answerDialog fixture a dismiss ')`);
    await act(asks("", ""));
    await placed("the dialog was answered");
    await until(`${state}.calls.includes('command fixture a focus')`);
    assert.deepEqual((await calls()).filter(call => !call.startsWith("capture")), ["answerDialog fixture a dismiss ", "command fixture a focus"]);
    assert.equal(await evaluate(dialog), null);
    // A prompt has a field with what the page proposes; Enter accepts with what was typed.
    await act(asks("a", "{ kind: 'prompt', message: 'Your name?', defaultText: 'Ada' }"));
    await hidden("a page that waits in a prompt");
    assert.deepEqual(await evaluate(`[${dialog}.querySelector('input').value, document.activeElement === ${dialog}.querySelector('input'), ${dialog}.querySelector('input').getAttribute('aria-label')]`), ["Ada", true, "Answer for the page"]);
    await act(`const field = ${dialog}.querySelector('input'); Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(field, 'Grace'); field.dispatchEvent(new Event('input', { bubbles: true }))`);
    await key("Enter", 13);
    await until(`${state}.calls.includes('answerDialog fixture a accept Grace')`);
    assert.ok(await evaluate(`[...${dialog}.querySelectorAll('button')].every(button => button.disabled)`), "an answer is given once");
    await act(asks("", ""));
    await placed("the prompt was answered");
    // An alert has one button; the question before a page is left has words of its own.
    await act(asks("a", "{ kind: 'alert', message: 'Saved.', defaultText: null }"));
    assert.deepEqual(await evaluate(`[...${dialog}.querySelectorAll('button')].map(button => button.textContent)`), ["OK"]);
    await act(`${dialog}.querySelector('button').click()`);
    await until(`${state}.calls.includes('answerDialog fixture a accept ')`);
    await act(asks("a", "{ kind: 'beforeunload', message: '', defaultText: null }"));
    assert.deepEqual(await evaluate(`[${dialog}.querySelector('p').textContent, [...${dialog}.querySelectorAll('button')].map(button => button.textContent)]`), ["Leave this page? Changes you made may not be saved.", ["Stay", "Leave"]]);
    // What is typed elsewhere in the window is left alone when a page asks.
    await act(asks("", ""));
    await act("const other = document.createElement('textarea'); other.id = 'elsewhere'; document.body.append(other); other.focus()");
    await act(asks("a", "{ kind: 'alert', message: 'Hello', defaultText: null }"));
    assert.equal(await evaluate("document.activeElement.id"), "elsewhere");
    await act("document.getElementById('elsewhere').remove()");
    // A page behind that asks is marked on its tab, and the page in front stays shown.
    await act(asks("b", "{ kind: 'alert', message: 'Behind', defaultText: null }"));
    await placed("a page behind asks");
    assert.equal(await evaluate(dialog), null);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.browser-tab')].map(tab => tab.querySelector('.browser-tab-mark')?.getAttribute('aria-label') ?? null)"), [null, "This page asks a question"]);
    await act(asks("", ""));
    await calls();

    // Annotations. The toolbar turns pick mode on and off in the page in front, and says so while it is on.
    const annotations = (value: string) => `${f}.setAnnotations(${value})`;
    const some = (list: string, more = "") => `{ sessionId: 'fixture', annotations: ${list}, pickingPageId: null, pending: null, open: ${list}.filter(item => item.status === 'open').length${more} }`;
    const panel = "document.querySelector('.browser-annotations')";
    await calls();
    assert.equal(await evaluate(panel), null);
    assert.equal(await evaluate("document.querySelector('.browser-annotate .browser-count')"), null);
    await act(`${button("Annotate an element of the page")}.click()`);
    assert.deepEqual(await asked(), ["pick fixture a true"]);
    await act(annotations(some("[]", ", pickingPageId: 'a'")));
    assert.equal(await evaluate("document.querySelector('.browser-picking').textContent"), "Click an element of the page to annotate it. Escape ends.");
    assert.equal(await evaluate(`${button("Stop annotating")}.getAttribute('aria-pressed')`), "true");
    await placed("the page stays shown while an element is picked");
    await act(`${button("Stop annotating")}.click()`);
    assert.deepEqual(await asked(), ["pick fixture a false"]);
    // An element was picked: the list opens under the page, which gets less room, with what the page reported and a field.
    const tall = (await evaluate(`${view}.getBoundingClientRect().height`)) as number;
    await act(`${state}.still = 'data:image/gif;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw=='`);
    await act(annotations(some("[]", ", pending: { pageId: 'a', address: 'https://example.com/a', title: 'Page a', element: " + `${f}.element('button', 'Pay now')` + ", hasImage: true }")));
    await until(`${panel} && ${view}.getBoundingClientRect().height < ${tall}`);
    await placed("the page above the annotations");
    assert.ok(await evaluate(`${panel}.getBoundingClientRect().top >= ${view}.getBoundingClientRect().bottom - 0.5`));
    assert.deepEqual(await evaluate(`[${panel}.querySelector('.browser-annotation-picked .browser-annotation-element').textContent, ${panel}.querySelector('.browser-annotation-picked code').textContent]`), ["<button> Pay now", "#pay-now"]);
    await until(`${panel}.querySelector('.browser-annotation-picked img') !== null`);
    assert.ok(await evaluate(`document.activeElement === ${panel}.querySelector('textarea')`), "the text is typed here");
    assert.equal(await evaluate(`[...${panel}.querySelectorAll('button')].find(item => item.textContent === 'Save annotation').disabled`), true, "an annotation needs a text");
    await calls();
    await act(`const field = ${panel}.querySelector('textarea'); Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(field, 'Make it green'); field.dispatchEvent(new Event('input', { bubbles: true }))`);
    await act(`[...${panel}.querySelectorAll('button')].find(item => item.textContent === 'Save annotation').click()`);
    assert.deepEqual(await asked(), ["saveAnnotation fixture Make it green"]);
    // Escape in the field forgets what was picked.
    await act(`${panel}.querySelector('textarea').focus()`);
    await key("Escape", 27);
    assert.deepEqual(await asked(), ["discardPick fixture"]);
    // The list: the open ones as the host orders them, what is not on the page said, a resolution with who and why.
    await act(annotations(some(`[${f}.annotation(2, { text: 'Bigger title', hasImage: true }), ${f}.annotation(3, { found: false }), ${f}.annotation(1, { status: 'resolved', resolvedBy: 'agent', note: 'Done in app.css', found: null })]`)));
    assert.ok(await evaluate(`${panel} !== null && ${panel}.querySelector('form') === null`), "the list stays open with what was saved");
    assert.equal(await evaluate("document.querySelector('.browser-annotate .browser-count').textContent"), "2");
    assert.equal(await evaluate(`${button("Show or hide the annotations")}.getAttribute('aria-pressed')`), "true");
    assert.deepEqual(await evaluate(`[...${panel}.querySelectorAll('.browser-annotation')].map(item => [item.querySelector('.browser-annotation-number').textContent, item.querySelector('.browser-annotation-text').textContent, item.dataset.status,
      item.querySelector('.browser-annotation-missing')?.textContent ?? null, item.querySelector('.browser-annotation-resolved')?.textContent ?? null])`),
      [["2", "Bigger title", "open", null, null], ["3", "Change 3", "open", "Not found on this page", null], ["1", "Change 1", "resolved", null, "Resolved by the agent: Done in app.css"]]);
    assert.equal(await evaluate(`${panel}.querySelector('.browser-annotations-count').textContent`), "2 open");
    assert.equal(await evaluate(`[...${panel}.querySelectorAll('button')].every(item => !!item.title || !!item.textContent)`), true);
    await until(`${panel}.querySelector('.browser-annotation img') !== null`);
    await calls();
    const rowButton = (row: number, label: string) => `${panel}.querySelectorAll('.browser-annotation')[${row}].querySelector('button[aria-label="${label}"]')`;
    await act(`${rowButton(0, "Go to the element")}.click(); ${rowButton(0, "Resolve")}.click(); ${rowButton(2, "Reopen")}.click(); ${rowButton(1, "Delete")}.click()`);
    assert.deepEqual(await asked(), ["changeAnnotation fixture 2 reveal", "changeAnnotation fixture 2 resolve", "changeAnnotation fixture 1 reopen", "changeAnnotation fixture 3 delete"]);
    // Add to prompt writes the annotations after what the composer holds, and hands their pictures over as a paste.
    await act(`window.pasted = []; window.drafts = [];
      window.addEventListener('codealta:plugin', event => { const request = event.detail; if (request.sessionId !== 'fixture') return; request.handled = true; request.result = true;
        if (request.kind === 'state') request.state = { sessionId: 'fixture', draftText: 'Please fix these.', busy: false }; else if (request.kind === 'draft') window.drafts.push(request.text); });
      document.getElementById('session-prompt-fixture').addEventListener('paste', event => { window.pasted.push([...event.clipboardData.files].map(file => file.name + ' ' + file.type)); event.preventDefault(); })`);
    await act(`${state}.prompt = { status: 'ok', text: 'The annotations', pictures: [2] }; ${state}.still = 'data:image/png;base64,iVBORw0KGgo='`);
    await act(`[...${panel}.querySelectorAll('button')].find(item => item.textContent === 'Add to prompt').click()`);
    await until("document.querySelector('.browser-notice')?.textContent === 'The annotations were added to the prompt.'");
    assert.deepEqual(await evaluate("[window.drafts, window.pasted]"), [["Please fix these.\n\nThe annotations"], [["annotation-2.png image/png"]]]);
    // The list keeps the frame inside its pane in every language, and closes.
    for (const locale of ["de", "ja", "en"]) {
      await act(`${f}.setSize({ width: 700, height: 500 }); ${f}.setLocale('${locale}')`);
      assert.equal(await evaluate(`[document.querySelector('.browser-pane'), document.querySelector('.browser-bar'), ${panel}, ${panel}.querySelector('.browser-annotations-bar')].every(item => item.scrollWidth <= item.clientWidth)`), true, `annotations ${locale}`);
    }
    await act(`${f}.setSize({ width: 1000, height: 600 })`);
    await act(`${button("Hide the annotations")}.click()`);
    assert.equal(await evaluate(panel), null);
    await act(annotations("null"));
    await calls();

    // A session without a page: the address field in the middle, and no view anywhere.
    await act(`${f}.setSession({ sessionId: 'fixture', activePageId: null, pages: [] })`);
    await hidden("no page to show");
    assert.equal(await evaluate(view), null);
    assert.ok(await evaluate("document.querySelector('.browser-empty .browser-address') !== null && document.querySelector('.browser-empty p').textContent.length > 0"));
    assert.equal(await evaluate(`${button("Close the browser")} !== null`), true);
    await type("localhost:5173"); await key("Enter", 13);
    await until(`${state}.calls.includes('command fixture new focus')`);
    assert.deepEqual(await asked(), ["open fixture localhost:5173", "command fixture new focus"]);

    // Both themes and every language keep the frame inside its pane, down to the least width of the browser.
    await act(`${f}.setSession({ sessionId: 'fixture', activePageId: 'a', pages: ['a', 'b', 'c', 'd', 'e'].map(id => ${f}.page(id)) })`);
    for (const dark of [true, false]) {
      await act(`document.documentElement.classList.toggle('bp6-dark', ${dark}); document.documentElement.dataset.theme = '${dark ? "dark" : "light"}'`);
      for (const width of [1100, 700])
        for (const locale of ["en", "es", "fr", "de", "ja", "zh-CN"]) {
          await act(`${f}.setSize({ width: ${width}, height: 500 }); ${f}.setLocale('${locale}')`);
          assert.equal(await evaluate("[document.querySelector('.browser-pane'), document.querySelector('.browser-strip'), document.querySelector('.browser-bar')].every(element => element.scrollWidth <= element.clientWidth)"), true, `${width}px ${locale}`);
          assert.ok(await evaluate(`(() => { const pane = document.querySelector('.browser-pane').getBoundingClientRect(), box = document.querySelector('.browser-close').getBoundingClientRect();
            return box.width > 0 && box.right <= pane.right + 0.5 && box.left >= pane.left; })()`), `${width}px ${locale}: the close button is inside the pane`);
        }
    }
    assert.ok(await evaluate("document.querySelector('.browser-pane').getBoundingClientRect().width >= 320"));
    await act(`${f}.setLocale('en'); ${f}.setSize({ width: 1100, height: 600 })`);
    await placed("the pane as it was");

    // A system without browser views says so in one line, and places nothing.
    await act(`${f}.setSupported(false)`);
    await hidden("a system without browser views");
    assert.equal(await evaluate("document.querySelector('.browser-unsupported').textContent"), "This system cannot show a browser inside the window.");
    assert.equal(await evaluate(`${button("Close the browser")} !== null`), true);
    await act(`${f}.setSupported(true)`);
    await placed("a system with them");

    // The pane goes away: its view is hidden.
    await act(`${f}.setMounted(false)`);
    await hidden("a pane that is gone");
  } finally {
    socket?.close(); browser?.kill();
    if (browser && browser.exitCode === null) await new Promise(resolve => browser!.once("exit", resolve));
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
