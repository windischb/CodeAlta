import assert from "node:assert/strict";
import test from "node:test";
import type { BrowserAnnotationItem, BrowserAnnotationsItem, BrowserEvent, BrowserPageItem, BrowserReply, BrowserSessionItem } from "#neoastra";
import { createBrowserHub, reconnectMilliseconds, type BrowserApi } from "./browserHub";

const page = (id: string, more: Partial<BrowserPageItem> = {}): BrowserPageItem => ({ id, address: `https://example.com/${id}`, title: id, loading: false, canGoBack: false, canGoForward: false, live: true, dialog: null, ...more });
const browserOf = (sessionId: string, pages: BrowserPageItem[], activePageId: string | null = pages[0]?.id ?? null): BrowserSessionItem => ({ sessionId, pages, activePageId });
const ready = (supported = true): BrowserEvent => ({ kind: "ready", supported, session: null, annotations: null });
const told = (session: BrowserSessionItem): BrowserEvent => ({ kind: "session", supported: true, session, annotations: null });
const annotation = (number: number, more: Partial<BrowserAnnotationItem> = {}): BrowserAnnotationItem => ({ number, text: `Change ${number}`, status: "open", resolvedBy: null, note: null,
  address: "https://example.com/a", title: "a", element: { selector: `#e${number}`, tag: "button", role: "button", name: "Pay", text: "Pay", html: "<button>" }, hasImage: true, found: true, ...more });
const annotationsOf = (sessionId: string, annotations: BrowserAnnotationItem[], more: Partial<BrowserAnnotationsItem> = {}): BrowserAnnotationsItem =>
  ({ sessionId, annotations, pickingPageId: null, pending: null, open: annotations.filter(item => item.status === "open").length, ...more });
const annotated = (annotations: BrowserAnnotationsItem): BrowserEvent => ({ kind: "annotations", supported: true, session: null, annotations });
const settle = async () => { for (let turn = 0; turn < 20; turn++) await Promise.resolve(); };

// A host that tells what a test gives it, records what it is asked, and timers a test runs by hand. The
// placements wait until the test answers them; every other call is answered at once.
function fixture() {
  const calls: string[] = [];
  const watches: Array<{ push: (value: BrowserEvent) => void; end: () => void }> = [];
  const placements: Array<(status?: string) => void> = [];
  const timers: Array<{ run: () => void; milliseconds: number }> = [];
  const answers = new Map<string, BrowserReply>();
  // The annotations of a session are read with its pages: these reads are counted apart from the other calls.
  const reads: string[] = [];
  const replies = { annotations: null as BrowserAnnotationsItem | null, status: "ok", image: "data:image/png;base64,AA==" as string | null };
  const annotate = (name: string) => (request: object) => {
    calls.push(`${name} ${JSON.stringify(request)}`);
    return failing ? Promise.reject(Error("gone")) : Promise.resolve({ status: replies.status, annotations: replies.annotations });
  };
  let failing = false;
  const reply = (name: string) => (request: object) => {
    calls.push(`${name} ${JSON.stringify(request)}`);
    return failing ? Promise.reject(Error("gone")) : Promise.resolve(answers.get(name) ?? { status: "ok", session: null, pageId: null });
  };
  const api = {
    watch: () => {
      const queue: BrowserEvent[] = [];
      let wake: (() => void) | null = null, ended = false;
      watches.push({ push: value => { queue.push(value); wake?.(); }, end: () => { ended = true; wake?.(); } });
      return Promise.resolve((async function* () {
        while (true) {
          while (queue.length > 0) yield queue.shift()!;
          if (ended) return;
          await new Promise<void>(resolve => { wake = resolve; });
        }
      })());
    },
    state: reply("state"), open: reply("open"), close: reply("close"), select: reply("select"), move: reply("move"),
    navigate: reply("navigate"), command: reply("command"), release: reply("release"), answerDialog: reply("answerDialog"),
    annotations: (request: object) => { reads.push(JSON.stringify(request)); return Promise.resolve({ status: "ok", annotations: replies.annotations }); },
    pick: annotate("pick"), saveAnnotation: annotate("saveAnnotation"), discardPick: annotate("discardPick"), changeAnnotation: annotate("changeAnnotation"),
    annotationImage: (request: object) => { calls.push(`annotationImage ${JSON.stringify(request)}`); return Promise.resolve({ status: replies.image ? "ok" : "unavailable_page", image: replies.image, width: 0, height: 0 }); },
    annotationPrompt: (request: object) => { calls.push(`annotationPrompt ${JSON.stringify(request)}`); return Promise.resolve({ status: "ok", text: "The annotations", pictures: [1] }); },
    place: (request: object) => {
      calls.push(`place ${JSON.stringify(request)}`);
      return new Promise<BrowserReply>(resolve => placements.push((status = "ok") => resolve({ status, session: null, pageId: null })));
    },
    capture: (request: object) => { calls.push(`capture ${JSON.stringify(request)}`); return Promise.resolve({ status: answers.get("capture")?.status ?? "ok", image: "data:image/jpeg;base64,AA==", width: 8, height: 6 }); },
  } as unknown as BrowserApi;
  const hub = createBrowserHub(api, { set: (run, milliseconds) => { const timer = { run, milliseconds }; timers.push(timer); return timer; }, clear: timer => { const index = timers.indexOf(timer as typeof timers[number]); if (index >= 0) timers.splice(index, 1); } });
  return { hub, calls, watches, placements, timers, answers, reads, replies,
    fail: (value: boolean) => { failing = value; },
    take: () => calls.splice(0),
    // Answers the placements that wait, oldest first.
    answer: async (status = "ok") => { for (const resolve of placements.splice(0)) resolve(status); await settle(); },
    fire: (milliseconds: number) => {
      const due = timers.filter(candidate => candidate.milliseconds === milliseconds);
      for (const timer of due) timers.splice(timers.indexOf(timer), 1);
      for (const timer of due) timer.run();
    } };
}
const placed = (sessionId: string, x: number, y = 0, width = 100, height = 50) => `place ${JSON.stringify({ expectedEpoch: "epoch", sessionId, visible: true, x, y, width, height })}`;
const hid = (sessionId: string, expectedEpoch = "epoch") => `place ${JSON.stringify({ expectedEpoch, sessionId, visible: false, x: 0, y: 0, width: 0, height: 0 })}`;

test("the page is told whether the system has browsers, and the browser of each session it asked for", async () => {
  const { hub, watches, take } = fixture();
  let changes = 0;
  hub.subscribe(() => { changes++; });
  assert.equal(hub.supported(), null);
  assert.equal(hub.session("s1"), null);
  // Asked before the host is there: asked for once it is.
  hub.ensure("s1");
  assert.deepEqual(take(), []);
  const stop = hub.connect("epoch");
  await settle();
  assert.deepEqual(take(), []);
  watches[0].push(ready());
  await settle();
  assert.equal(hub.supported(), true);
  assert.deepEqual(take(), ['state {"expectedEpoch":"epoch","sessionId":"s1"}']);
  // Asked again by the pane, by another pane of the session: the host was asked already.
  hub.ensure("s1");
  hub.ensure("s2");
  await settle();
  assert.deepEqual(take(), ['state {"expectedEpoch":"epoch","sessionId":"s2"}']);
  const first = browserOf("s1", [page("a"), page("b")]);
  watches[0].push(told(first));
  await settle();
  assert.equal(hub.session("s1"), first);
  const seen = changes;
  // The same browser told again changes nothing: what a component read stays what it reads.
  watches[0].push(told(browserOf("s1", [page("a"), page("b")])));
  await settle();
  assert.equal(hub.session("s1"), first);
  assert.equal(changes, seen);
  watches[0].push(told(browserOf("s1", [page("a", { loading: true }), page("b")])));
  await settle();
  assert.equal(hub.session("s1")?.pages[0].loading, true);
  assert.equal(changes, seen + 1);
  stop();
});

test("a JavaScript dialog of a page is told as it opens and goes, and answered through the host", async () => {
  const { hub, watches, take, answers } = fixture();
  hub.ensure("s1");
  const stop = hub.connect("epoch");
  await settle();
  watches[0].push(ready());
  watches[0].push(told(browserOf("s1", [page("a"), page("b")])));
  await settle();
  take();
  let changes = 0;
  hub.subscribe(() => { changes++; });
  const asking = { kind: "prompt", message: "Your name?", defaultText: "Ada" };
  watches[0].push(told(browserOf("s1", [page("a", { dialog: asking }), page("b")])));
  await settle();
  assert.deepEqual(hub.session("s1")?.pages[0].dialog, asking);
  assert.equal(changes, 1);
  // The same dialog told again is no change; another message is.
  watches[0].push(told(browserOf("s1", [page("a", { dialog: { ...asking } }), page("b")])));
  await settle();
  assert.equal(changes, 1);
  watches[0].push(told(browserOf("s1", [page("a", { dialog: { ...asking, message: "Your age?" } }), page("b")])));
  await settle();
  assert.equal(changes, 2);
  // The answer goes to the host, whose reply says the dialog is gone.
  answers.set("answerDialog", { status: "ok", session: browserOf("s1", [page("a"), page("b")]), pageId: null });
  assert.equal((await hub.answerDialog("s1", "a", true, "Grace")).status, "ok");
  assert.equal((await hub.answerDialog("s1", "a", false)).status, "ok");
  assert.deepEqual(take(), ['answerDialog {"expectedEpoch":"epoch","sessionId":"s1","pageId":"a","accept":true,"text":"Grace"}',
    'answerDialog {"expectedEpoch":"epoch","sessionId":"s1","pageId":"a","accept":false,"text":null}']);
  assert.equal(hub.session("s1")?.pages[0].dialog, null);
  assert.equal(changes, 3);
  stop();
});

test("the annotations of a session are read with its pages, told as they change, and changed through the host", async () => {
  const { hub, watches, take, reads, replies } = fixture();
  replies.annotations = annotationsOf("s1", [annotation(1)]);
  hub.ensure("s1");
  const stop = hub.connect("epoch");
  await settle();
  assert.equal(hub.annotations("s1"), null);
  watches[0].push(ready());
  await settle();
  // Asked for with the pages, once: the count on the buttons of the session needs no browser part.
  assert.deepEqual(reads, ['{"expectedEpoch":"epoch","sessionId":"s1"}']);
  assert.equal(hub.annotations("s1")?.open, 1);
  take();
  let changes = 0;
  hub.subscribe(() => { changes++; });
  const first = hub.annotations("s1");
  // The same annotations told again are no change; a pick that is going on, an element that waits, a resolution are.
  watches[0].push(annotated(annotationsOf("s1", [annotation(1)])));
  await settle();
  assert.equal(hub.annotations("s1"), first);
  assert.equal(changes, 0);
  watches[0].push(annotated(annotationsOf("s1", [annotation(1)], { pickingPageId: "a" })));
  await settle();
  assert.equal(hub.annotations("s1")?.pickingPageId, "a");
  watches[0].push(annotated(annotationsOf("s1", [annotation(1, { found: false })])));
  await settle();
  assert.equal(hub.annotations("s1")?.annotations[0].found, false);
  assert.equal(changes, 2);
  // A session that has none, and never had, is not told of.
  watches[0].push(annotated(annotationsOf("s2", [])));
  await settle();
  assert.equal(hub.annotations("s2"), null);
  assert.equal(changes, 2);

  // What the pane asks goes to the host, and what the host answers is what the session has.
  replies.annotations = annotationsOf("s1", [annotation(1), annotation(2)]);
  assert.equal((await hub.pick("s1", "a", true)).status, "ok");
  assert.equal((await hub.saveAnnotation("s1", "Make it blue")).status, "ok");
  assert.equal((await hub.discardPick("s1")).status, "ok");
  assert.equal((await hub.changeAnnotation("s1", 2, "resolve")).status, "ok");
  assert.deepEqual(take(), ['pick {"expectedEpoch":"epoch","sessionId":"s1","pageId":"a","on":true}', 'saveAnnotation {"expectedEpoch":"epoch","sessionId":"s1","text":"Make it blue"}',
    'discardPick {"expectedEpoch":"epoch","sessionId":"s1"}', 'changeAnnotation {"expectedEpoch":"epoch","sessionId":"s1","number":2,"action":"resolve"}']);
  assert.equal(hub.annotations("s1")?.annotations.length, 2);
  replies.status = "limit";
  assert.equal((await hub.saveAnnotation("s1", "One more")).status, "limit");
  replies.status = "ok";
  take();

  // The picture of an annotation is read once; the one of the element that was picked each time, since it changes.
  assert.equal(await hub.annotationImage("s1", 2), "data:image/png;base64,AA==");
  assert.equal(await hub.annotationImage("s1", 2), "data:image/png;base64,AA==");
  await hub.annotationImage("s1", 0);
  await hub.annotationImage("s1", 0);
  assert.deepEqual(take().map(call => call.replace(/^(\w+) .*"number":(\d+).*$/, "$1 $2")), ["annotationImage 2", "annotationImage 0", "annotationImage 0"]);
  // An annotation that is gone takes its picture with it.
  watches[0].push(annotated(annotationsOf("s1", [annotation(1)])));
  await settle();
  replies.image = null;
  assert.equal(await hub.annotationImage("s1", 2), null);
  assert.deepEqual((await hub.annotationPrompt("s1")), { status: "ok", text: "The annotations", pictures: [1] });
  take();

  // A session whose tab is closed is forgotten with its annotations.
  const before = changes;
  hub.release("s1");
  assert.equal(hub.annotations("s1"), null);
  assert.equal(changes, before + 1);
  stop();
  assert.equal((await hub.pick("s1", "a", true)).status, "unavailable");
});

test("a system without browser views says so", async () => {
  const { hub, watches } = fixture();
  hub.connect("epoch");
  await settle();
  watches[0].push(ready(false));
  await settle();
  assert.equal(hub.supported(), false);
});

test("what a call answers is the browser of the session at once, without the watch", async () => {
  const { hub, watches, take, answers, fail } = fixture();
  assert.equal((await hub.open("s1", "example.com")).status, "unavailable", "no host yet");
  hub.connect("epoch");
  await settle();
  watches[0].push(ready());
  await settle();
  answers.set("open", { status: "ok", session: browserOf("s1", [page("a")]), pageId: "a" });
  const opened = await hub.open("s1", "example.com");
  assert.deepEqual([opened.status, opened.pageId], ["ok", "a"]);
  assert.deepEqual(hub.session("s1")?.pages.map(value => value.id), ["a"]);
  answers.set("navigate", { status: "ok", session: browserOf("s1", [page("a", { address: "https://example.org/", loading: true })]), pageId: null });
  await hub.navigate("s1", "a", "example.org");
  assert.equal(hub.session("s1")?.pages[0].address, "https://example.org/");
  await hub.select("s1", "a"); await hub.move("s1", "a", 0); await hub.command("s1", "a", "reload"); await hub.close("s1", "a");
  assert.deepEqual(take(), [
    'open {"expectedEpoch":"epoch","sessionId":"s1","address":"example.com"}',
    'navigate {"expectedEpoch":"epoch","sessionId":"s1","pageId":"a","address":"example.org"}',
    'select {"expectedEpoch":"epoch","sessionId":"s1","pageId":"a"}',
    'move {"expectedEpoch":"epoch","sessionId":"s1","pageId":"a","index":0}',
    'command {"expectedEpoch":"epoch","sessionId":"s1","pageId":"a","command":"reload"}',
    'close {"expectedEpoch":"epoch","sessionId":"s1","pageId":"a"}']);
  // A refusal is told as it is, and the browser stays what it was.
  answers.set("open", { status: "limit", session: null, pageId: null });
  assert.equal((await hub.open("s1")).status, "limit");
  assert.deepEqual(hub.session("s1")?.pages.map(value => value.id), ["a"]);
  // A call that does not come back failed.
  fail(true);
  assert.equal((await hub.open("s1")).status, "failed");
  fail(false);
  assert.deepEqual(await hub.capture("s1", "a"), { image: "data:image/jpeg;base64,AA==", width: 8, height: 6 });
  answers.set("capture", { status: "unavailable_page", session: null, pageId: null });
  assert.equal(await hub.capture("s1", "a"), null);
});

test("of several placements asked for in a row the last one is sent, one call at a time, and none twice", async () => {
  const { hub, watches, take, answer } = fixture();
  hub.connect("epoch");
  await settle();
  watches[0].push(ready());
  await settle();
  // A session that was never placed has no view to hide.
  hub.place("s1", null);
  assert.deepEqual(take(), []);
  hub.place("s1", { x: 1, y: 0, width: 100, height: 50 });
  hub.place("s1", { x: 2, y: 0, width: 100, height: 50 });
  hub.place("s1", { x: 3, y: 0, width: 100, height: 50 });
  // Another session does not wait for this one.
  hub.place("s2", { x: 9, y: 0, width: 100, height: 50 });
  await settle();
  assert.deepEqual(take(), [placed("s1", 1), placed("s2", 9)]);
  await answer();
  assert.deepEqual(take(), [placed("s1", 3)]);
  await answer();
  assert.deepEqual(take(), []);
  // The same placement again is not sent.
  hub.place("s1", { x: 3, y: 0, width: 100, height: 50 });
  await settle();
  assert.deepEqual(take(), []);
  // Hidden, and shown again where it was while the hiding is on its way.
  hub.place("s1", null);
  hub.place("s1", { x: 3, y: 0, width: 100, height: 50 });
  await settle();
  assert.deepEqual(take(), [hid("s1")]);
  await answer();
  assert.deepEqual(take(), [placed("s1", 3)]);
  await answer();
  hub.place("s1", null);
  hub.place("s1", null);
  await settle();
  assert.deepEqual(take(), [hid("s1")]);
  await answer();
  assert.deepEqual(take(), []);
});

test("a placement the host does not take leaves the view hidden", async () => {
  const { hub, watches, take, answer } = fixture();
  hub.connect("epoch");
  await settle();
  watches[0].push(ready());
  await settle();
  hub.place("s1", { x: 1, y: 0, width: 100, height: 50 });
  await settle();
  await answer("invalid");
  assert.deepEqual(take(), [placed("s1", 1), hid("s1")]);
  await answer();
  // The same one is not tried again; another one is.
  hub.place("s1", { x: 1, y: 0, width: 100, height: 50 });
  await settle();
  assert.deepEqual(take(), []);
  hub.place("s1", { x: 2, y: 0, width: 100, height: 50 });
  await settle();
  assert.deepEqual(take(), [placed("s1", 2)]);
});

test("after the host stopped telling, the page listens again, asks again and says again where each view is", async () => {
  const { hub, watches, take, answer, fire, timers } = fixture();
  hub.ensure("s1");
  hub.connect("epoch");
  await settle();
  watches[0].push(ready());
  await settle();
  hub.place("s1", { x: 1, y: 0, width: 100, height: 50 });
  hub.place("s2", { x: 2, y: 0, width: 100, height: 50 });
  await settle();
  await answer();
  hub.place("s2", null);
  await settle();
  await answer();
  take();
  watches[0].end();
  await settle();
  assert.deepEqual(timers.map(timer => timer.milliseconds), [reconnectMilliseconds]);
  // A session asked for while nothing listens is asked for once the host is back.
  hub.ensure("s3");
  assert.deepEqual(take(), []);
  fire(reconnectMilliseconds);
  await settle();
  assert.equal(watches.length, 2);
  watches[1].push(ready());
  await settle();
  // The shown session is placed again, and the one that is not shown is hidden again: when in doubt, hidden.
  assert.deepEqual(take().sort(), [placed("s1", 1), hid("s2"), 'state {"expectedEpoch":"epoch","sessionId":"s1"}', 'state {"expectedEpoch":"epoch","sessionId":"s3"}'].sort());
  await answer();
});

test("a page that stops listening hides the views it placed, and places them with the next host", async () => {
  const { hub, watches, take, answer } = fixture();
  const stop = hub.connect("epoch");
  await settle();
  watches[0].push(ready());
  await settle();
  hub.place("s1", { x: 1, y: 0, width: 100, height: 50 });
  hub.place("s2", { x: 2, y: 0, width: 100, height: 50 });
  await settle();
  await answer();
  hub.place("s2", null);
  await settle();
  await answer();
  take();
  stop();
  await settle();
  assert.deepEqual(take(), [hid("s1")]);
  await answer();
  // Without a host nothing is sent; what is wanted is kept.
  hub.place("s1", { x: 5, y: 0, width: 100, height: 50 });
  await settle();
  assert.deepEqual(take(), []);
  assert.equal((await hub.select("s1", "a")).status, "unavailable");
  hub.connect("next");
  await settle();
  watches[1].push(ready());
  await settle();
  assert.deepEqual(take(), [`place ${JSON.stringify({ expectedEpoch: "next", sessionId: "s1", visible: true, x: 5, y: 0, width: 100, height: 50 })}`]);
});

test("a session whose tab is closed lets its views go and is forgotten", async () => {
  const { hub, watches, take, answer, answers, fire } = fixture();
  hub.ensure("s1");
  hub.connect("epoch");
  await settle();
  watches[0].push(ready());
  await settle();
  answers.set("open", { status: "ok", session: browserOf("s1", [page("a")]), pageId: "a" });
  await hub.open("s1");
  hub.place("s1", { x: 1, y: 0, width: 100, height: 50 });
  await settle();
  await answer();
  take();
  let changes = 0;
  hub.subscribe(() => { changes++; });
  hub.release("s1");
  await settle();
  assert.deepEqual(take(), ['release {"expectedEpoch":"epoch","sessionId":"s1"}']);
  assert.equal(hub.session("s1"), null);
  assert.equal(changes, 1);
  // It is neither asked for nor placed when the host is listened to again.
  watches[0].end();
  await settle();
  fire(reconnectMilliseconds);
  await settle();
  watches[1].push(ready());
  await settle();
  assert.deepEqual(take(), []);
});
