import assert from "node:assert/strict";
import test from "node:test";
import { composerStatusItems, sameComposerStatus } from "./pluginStatus";

const mcp = { pluginId: "mcp", name: "mcp-status", label: "MCP", text: "2/3 · 1 unavailable · tools not loaded", tone: "warning", settingsPage: "mcp" };

test("composer status accepts the items of the asked project", () => {
  assert.deepEqual(composerStatusItems({ status: "ok", projectId: "p1", items: [mcp] }, "p1"), [
    { key: "mcp/mcp-status", pluginId: "mcp", label: "MCP", text: "2/3 · 1 unavailable · tools not loaded", tone: "warning", settingsPage: "mcp", commandId: null, alternatives: [] }]);
  assert.deepEqual(composerStatusItems({ status: "ok", projectId: null, items: [] }, null), []);
  assert.deepEqual(composerStatusItems({ status: "ok", items: [] }, null), []);
  // An item can name a plugin command that it runs when clicked.
  assert.equal(composerStatusItems({ status: "ok", projectId: null, items: [{ ...mcp, commandId: "source:Sample/command:hello" }] }, null)?.[0].commandId, "source:Sample/command:hello");
  assert.equal(composerStatusItems({ status: "ok", projectId: null, items: [{ ...mcp, commandId: "x".repeat(513) }] }, null)?.[0].commandId, null);
});

test("composer status refuses another project's answer, refusals and malformed answers", () => {
  assert.equal(composerStatusItems({ status: "ok", projectId: "p2", items: [mcp] }, "p1"), null);
  assert.equal(composerStatusItems({ status: "stale_epoch", projectId: "p1", items: [] }, "p1"), null);
  assert.equal(composerStatusItems({ status: "ok", projectId: "p1" }, "p1"), null);
  assert.equal(composerStatusItems(null, "p1"), null);
});

test("composer status leaves out malformed and repeated items and defaults an unknown tone", () => {
  const items = composerStatusItems({ status: "ok", projectId: null, items: [
    mcp, mcp, null, { ...mcp, name: "other", tone: "loud", settingsPage: "../x" }, { ...mcp, name: "bad", text: "two\nlines" },
    { ...mcp, name: "empty", label: "", text: "" }, { ...mcp, pluginId: "has space" }] }, null);
  assert.deepEqual(items?.map(item => [item.key, item.tone, item.settingsPage]), [["mcp/mcp-status", "warning", "mcp"], ["mcp/other", "info", null]]);
});

test("composer status compares what is shown", () => {
  const [item] = composerStatusItems({ status: "ok", projectId: null, items: [mcp] }, null)!;
  assert.equal(sameComposerStatus([item], [{ ...item }]), true);
  assert.equal(sameComposerStatus([item], [{ ...item, text: "3/3 · tools not loaded" }]), false);
  assert.equal(sameComposerStatus([item], []), false);
});

test("composer status keeps the other ways to run an item, and leaves out the malformed ones", () => {
  const browser = { pluginId: "plugins", name: "status-0", label: "Browser", text: "Open", tone: "muted", settingsPage: null, commandId: "browser/open",
    alternatives: [{ label: "Firefox", commandId: "browser/open", argument: "firefox" }, { label: "CodeAlta (built-in)", commandId: "browser/open", argument: "codealta" },
      { label: "", commandId: "browser/open", argument: "x" }, { label: "No command", commandId: "" }, "nonsense", { label: "Bad\u0001", commandId: "c" }] };
  const [item] = composerStatusItems({ status: "ok", projectId: null, items: [browser] }, null)!;
  assert.deepEqual(item.alternatives, [{ label: "Firefox", commandId: "browser/open", argument: "firefox" }, { label: "CodeAlta (built-in)", commandId: "browser/open", argument: "codealta" }]);
  const [same] = composerStatusItems({ status: "ok", projectId: null, items: [browser] }, null)!;
  assert.equal(sameComposerStatus([item], [same]), true);
  const [fewer] = composerStatusItems({ status: "ok", projectId: null, items: [{ ...browser, alternatives: browser.alternatives.slice(1) }] }, null)!;
  assert.equal(sameComposerStatus([item], [fewer]), false, "Another menu is another item.");
});
