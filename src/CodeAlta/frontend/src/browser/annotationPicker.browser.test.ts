import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { pathToFileURL } from "node:url";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "../browserTarget";

// The script the host adds to every document of a browser view. It is a file of the host, which embeds it: the
// test loads the same file into a page of its own.
const picker = new URL("../../../Desktop/Browser/annotation-picker.js", import.meta.url);

const document = `<!doctype html><html><head><meta charset="utf-8"><title>Shop</title>
<style>body { margin: 20px; font: 14px sans-serif; } .card { width: 300px; padding: 10px; border: 1px solid #888; } .tall { height: 1600px; } button { width: 120px; height: 30px; }</style></head><body>
<h1 id="headline">Checkout</h1>
<div class="card wide primary extra more" data-testid="order-summary"><p class="total">Total: <span>42</span></p><button id="pay" type="button">Pay now</button></div>
<ul class="items"><li>First</li><li>Second</li><li><a href="#third" aria-label="Third item">Third</a></li></ul>
<div id="twin">a</div><div id="twin">b</div>
<input name="email" type="email" placeholder="you@example.com"><button id="off" disabled>Disabled</button>
<div id="box" style="width: 200px; height: 80px; overflow: auto"><div style="height: 40px"></div><p id="inner" style="height: 20px; margin: 0">Inner</p><div style="height: 300px"></div></div>
<div id="slot"><span id="swap">Old</span></div>
<p id="long"></p><div class="tall"></div><footer><button class="secondary">Top</button></footer>
<script>
  document.getElementById("long").textContent = "word ".repeat(400);
  window.seen = [];
  for (const type of ["pointerdown", "mousedown", "pointerup", "mouseup", "click", "keydown"])
    window.addEventListener(type, event => window.seen.push(type + (event.key ? ":" + event.key : "")), true);
  document.getElementById("pay").addEventListener("click", () => { document.title = "paid"; });
</script></body></html>`;

test("the picker of annotations names elements, keeps the click that picks from the page, and draws the marks", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-annotation-picker-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    // The picker comes first, as in a browser view, where it is added at the start of the document.
    const page = join(root, "page.html");
    await writeFile(page, document.replace("<head>", `<head><script>${(await readFile(picker, "utf8")).replace(/<\/script/g, "<\\/script")}</script>`));
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
    const api = "window.__codealtaAnnotations";
    // What a function of the picker returns is a JSON text, as the host reads it.
    const ask = async (call: string) => JSON.parse(await evaluate(`${api}.${call}`) as string);
    const centre = async (selector: string) => await evaluate(`(() => { const box = document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect(); return { x: box.left + box.width / 2, y: box.top + box.height / 2 }; })()`) as { x: number; y: number };
    const move = async (selector: string) => { await command("Input.dispatchMouseEvent", { type: "mouseMoved", ...await centre(selector) }); };
    const click = async (selector: string) => {
      const at = await centre(selector);
      await command("Input.dispatchMouseEvent", { type: "mouseMoved", ...at });
      await command("Input.dispatchMouseEvent", { type: "mousePressed", ...at, button: "left", clickCount: 1 });
      await command("Input.dispatchMouseEvent", { type: "mouseReleased", ...at, button: "left", clickCount: 1 });
    };
    const seen = async () => await evaluate("window.seen.splice(0)") as string[];
    const layer = "document.querySelector('codealta-annotations')?.shadowRoot";

    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 900, height: 600, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    for (let turn = 0; turn < 100 && !(await evaluate(`typeof ${api} === 'object' && document.readyState === 'complete'`)); turn++) await new Promise(resolve => setTimeout(resolve, 50));

    // It does nothing until it is asked: the page gets its clicks, and nothing is drawn.
    assert.deepEqual(await ask("poll()"), { state: "off" });
    await click("#pay");
    assert.equal(await evaluate("document.title"), "paid");
    assert.ok((await seen()).includes("click"));
    assert.equal(await evaluate("document.querySelector('codealta-annotations')"), null);
    await evaluate("document.title = 'Shop'");

    // A selector: an id or a stable attribute where exactly one element has it, the path from such an element otherwise.
    const selector = async (of: string) => (await ask(`describe(${JSON.stringify(of)})`)).selector as string;
    assert.equal(await selector("#pay"), "#pay");
    assert.equal(await selector(".card"), 'div[data-testid="order-summary"]');
    assert.equal(await selector(".total span"), 'div[data-testid="order-summary"] > p > span');
    assert.equal(await selector(".items li:nth-child(2)"), "html > body > ul > li:nth-of-type(2)");
    assert.equal(await selector("input"), 'input[name="email"]');
    assert.equal(await selector(".items a"), 'a[aria-label="Third item"]');
    // An id two elements share names neither.
    assert.equal(await selector("div#twin + div#twin"), "html > body > div:nth-of-type(3)");
    for (const of of ["#pay", ".card", ".total span", ".items li:nth-child(2)", "div#twin + div#twin", "footer button"])
      assert.equal(await evaluate(`document.querySelector(${JSON.stringify(await selector(of))}) === document.querySelector(${JSON.stringify(of)})`), true, of);
    assert.equal(await evaluate(`${api}.describe('#no-such-element')`), "null");

    // What is kept of an element, each text bounded.
    const pay = await ask("describe('#pay')");
    assert.deepEqual([pay.tag, pay.role, pay.name, pay.text, pay.html, pay.title], ["button", "button", "Pay now", "Pay now", '<button id="pay" type="button">', "Shop"]);
    assert.deepEqual(pay.ancestors, ["html", "body", "div.card.wide.primary.extra"]);
    assert.deepEqual(pay.viewport, { width: 900, height: 600 });
    assert.ok(pay.rect.width === 120 && pay.rect.height === 30 && pay.rect.x > 0 && pay.rect.y > 0, JSON.stringify(pay.rect));
    assert.ok(String(pay.address).startsWith("file:"));
    const long = await ask("describe('#long')");
    assert.equal(long.text.length, 500);
    assert.ok(long.text.endsWith("…") && !/\s{2}/.test(long.text));
    assert.deepEqual([(await ask("describe('input')")).name, (await ask("describe('.items a')")).role, (await ask("describe('#headline')")).role], ["you@example.com", "link", "heading"]);

    // Pick mode: the element under the pointer is outlined, and the click that picks it does not reach the page.
    assert.deepEqual(await ask("pick(true)"), { state: "picking" });
    await seen();
    await move("#headline");
    assert.equal(await evaluate(`${layer}.querySelector('.label').textContent`), "h1#headline");
    assert.ok(await evaluate(`(() => { const box = ${layer}.querySelector('.outline').getBoundingClientRect(), of = document.querySelector('#headline').getBoundingClientRect(); return Math.abs(box.left - of.left) < 1 && Math.abs(box.width - of.width) < 1; })()`));
    assert.equal(await evaluate("getComputedStyle(document.body).cursor"), "crosshair");
    assert.deepEqual(await ask("poll()"), { state: "picking" });
    await click("#pay");
    assert.deepEqual(await seen(), [], "the page sees nothing of the click");
    assert.equal(await evaluate("document.title"), "Shop");
    const picked = await ask("poll()");
    assert.equal(picked.state, "picked");
    assert.deepEqual([picked.element.selector, picked.element.name, picked.element.role], ["#pay", "Pay now", "button"]);
    assert.equal(await evaluate(`${layer}.querySelector('.outline')`), null, "nothing is drawn over what is captured");
    assert.equal(await evaluate("getComputedStyle(document.body).cursor"), "auto");
    // The rectangle of what was picked follows the page.
    await evaluate("window.scrollTo(0, 40)");
    assert.equal((await ask("poll()")).element.rect.y, picked.element.rect.y - 40);
    await evaluate("window.scrollTo(0, 0)");
    // The mode is over with the pick: the next click is the page's.
    await new Promise(resolve => setTimeout(resolve, 500));
    await click("#pay");
    assert.equal(await evaluate("document.title"), "paid");
    await seen();

    // An element that gets no click is picked when the pointer is released over it.
    await ask("pick(true)");
    await click("#off");
    assert.equal((await ask("poll()")).element?.selector, "#off");

    // Escape ends the mode, and the page does not get the key; so does the host.
    await ask("pick(true)");
    await seen();
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    assert.deepEqual(await ask("poll()"), { state: "cancelled" });
    assert.deepEqual((await seen()).filter(type => type.startsWith("key")), []);
    await ask("pick(true)");
    assert.deepEqual(await ask("pick(false)"), { state: "off" });
    await new Promise(resolve => setTimeout(resolve, 500));
    await click("#headline");
    assert.ok((await seen()).includes("click"));

    // The marks: a frame over each element that is found and a numbered badge at its corner, and the numbers that were.
    const marked = await ask("mark([{ number: 1, selector: '#pay' }, { number: 7, selector: '#gone' }, { number: 3, selector: 'html > body > ul > li:nth-of-type(2)' }, { number: 4, selector: '][' }])");
    assert.deepEqual(marked.found, [1, 3]);
    assert.deepEqual(await evaluate(`[...${layer}.querySelectorAll('.mark')].map(mark => mark.textContent)`), ["1", "3"]);
    const frame = (index: number) => `${layer}.querySelectorAll('.frame')[${index}]`;
    // Where the mark of an element is, against where the element is; null when it is hidden.
    const markOf = (index: number, selector: string) => evaluate(`(() => { const box = ${frame(index)}, mark = ${layer}.querySelectorAll('.mark')[${index}];
      if (getComputedStyle(box).display === 'none') return getComputedStyle(mark).display === 'none' ? null : 'badge shown';
      const at = box.getBoundingClientRect(), badge = mark.getBoundingClientRect(), of = document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect();
      return { frame: [at.left - of.left, at.top - of.top, at.width - of.width, at.height - of.height].map(Math.round), badge: [badge.left - Math.max(0, at.left - 9), badge.top - Math.max(0, at.top - 9)].map(Math.round) }; })()`);
    const onIt = { frame: [0, 0, 0, 0], badge: [0, 0] };
    assert.deepEqual(await markOf(0, "#pay"), onIt);
    assert.deepEqual(await evaluate(`(() => { const style = getComputedStyle(${frame(0)}); return [style.borderTopWidth, style.borderTopStyle, style.backgroundColor, style.pointerEvents]; })()`),
      ["2px", "solid", "rgba(45, 114, 210, 0.3)", "none"]);
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('codealta-annotations')).pointerEvents`), "none");
    const frames = () => new Promise(resolve => setTimeout(resolve, 80));
    // They follow the page when it scrolls, at the next frame, and hide when their element is out of sight.
    await evaluate("window.scrollTo(0, 60)");
    await frames();
    assert.deepEqual(await markOf(0, "#pay"), onIt);
    await evaluate("window.scrollTo(0, 900)");
    await frames();
    assert.equal(await markOf(0, "#pay"), null, "an element out of sight has no mark");
    await evaluate("window.scrollTo(0, 0)");
    // The same list keeps the marks it drew; a mark follows an element inside a container that scrolls, clipped
    // by it, and is hidden once the container has scrolled it out of sight.
    await evaluate(`Object.assign(${frame(0)}.dataset, { kept: "yes" }), 0`);
    assert.deepEqual((await ask("mark([{ number: 1, selector: '#pay' }, { number: 5, selector: '#inner' }, { number: 6, selector: '#swap' }])")).found, [1, 5, 6]);
    assert.equal(await evaluate(`${layer}.querySelectorAll('.frame').length`), 3);
    assert.equal(await evaluate(`${frame(0)}.dataset.kept`), "yes");
    await frames();
    assert.deepEqual(await markOf(1, "#inner"), onIt);
    await evaluate("document.querySelector('#box').scrollTop = 50");
    await frames();
    assert.deepEqual((await markOf(1, "#inner") as { frame: number[] }).frame, [0, 10, 0, -10], "clipped at the top of the container");
    await evaluate("document.querySelector('#box').scrollTop = 200");
    await frames();
    assert.equal(await markOf(1, "#inner"), null);
    // An element the page replaces is found again by its selector; one that is gone loses its mark.
    await evaluate("document.querySelector('#slot').innerHTML = '<b>Before</b><span id=\"swap\">New and longer</span>'");
    await frames();
    assert.deepEqual(await markOf(2, "#swap"), onIt);
    await evaluate("document.querySelector('#swap').remove()");
    await frames();
    assert.equal(await markOf(2, "#pay"), null);

    // A click on a badge is not the page's: it is kept for the host, once, with where its element is.
    assert.deepEqual(await ask("opened()"), { number: null });
    await seen();
    const badge = await evaluate(`(() => { const box = ${layer}.querySelectorAll('.mark')[0].getBoundingClientRect(); return { x: box.left + box.width / 2, y: box.top + box.height / 2 }; })()`) as { x: number; y: number };
    await command("Input.dispatchMouseEvent", { type: "mousePressed", ...badge, button: "left", clickCount: 1 });
    await command("Input.dispatchMouseEvent", { type: "mouseReleased", ...badge, button: "left", clickCount: 1 });
    assert.deepEqual(await seen(), [], "the page sees nothing of a click on a badge");
    const opened = await ask("opened()");
    assert.equal(opened.number, 1);
    assert.deepEqual(opened.rect, (await ask("describe('#pay')")).rect);
    assert.deepEqual(opened.viewport, { width: 900, height: 600 });
    assert.deepEqual(await ask("opened()"), { number: null });
    assert.deepEqual((await ask("mark([])")).found, []);
    assert.equal(await evaluate(`${layer}.querySelectorAll('.mark, .frame').length`), 0);

    // Going to an element scrolls it into view, unless it is in sight, and says where it is then.
    const shown = await ask("reveal('#pay', false)");
    assert.deepEqual([shown.found, shown.scrolled, shown.rect], [true, false, (await ask("describe('#pay')")).rect]);
    const revealed = await ask("reveal('footer button')");
    assert.deepEqual([revealed.found, revealed.scrolled, revealed.viewport], [true, true, { width: 900, height: 600 }]);
    assert.ok(await evaluate("window.scrollY > 500 && document.querySelector('footer button').getBoundingClientRect().top < window.innerHeight"));
    assert.equal(revealed.rect.y, await evaluate("Math.round(document.querySelector('footer button').getBoundingClientRect().top)"));
    assert.deepEqual(await ask("reveal('#gone')"), { found: false });
  } finally {
    socket?.close(); browser?.kill();
    if (browser && browser.exitCode === null) await new Promise(resolve => browser!.once("exit", resolve));
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
