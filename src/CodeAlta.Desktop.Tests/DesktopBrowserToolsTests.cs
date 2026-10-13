using System.Text.Json;
using CodeAlta.Desktop.Browser;
using CodeAlta.Desktop.Ui;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The tools a session drives the browser of its own session with: what they are, which pages a session reaches,
/// what is passed to the automation, and what a session whose commands are reviewed may do.
/// </summary>
[TestClass]
public sealed class DesktopBrowserToolsTests
{
    [TestMethod]
    public void Tools_AreTheOnesOfTheAutomation_WithThePrefix_AndSayWhatHoldsInTheBrowserOfASession()
    {
        var tools = DesktopBrowserTools.Describe(FakeBrowserUi.Automation);

        CollectionAssert.AreEqual(new[]
        {
            "browser_click", "browser_close_page", "browser_evaluate_script", "browser_get_console_message", "browser_handle_dialog", "browser_list_console_messages",
            "browser_list_pages", "browser_navigate_page", "browser_new_page", "browser_select_page", "browser_take_screenshot", "browser_take_snapshot",
        }, tools.Select(static tool => tool.Name).ToArray(), "sorted by name, without the tool that resizes a window");
        DesktopUiTool Tool(string name) => tools.Single(tool => tool.Name == name);

        // What reads: what the automation says so of, and the two that give what a page shows. Selecting a page changes what the user sees.
        CollectionAssert.AreEqual(new[] { "browser_get_console_message", "browser_list_console_messages", "browser_list_pages", "browser_take_screenshot", "browser_take_snapshot" },
            tools.Where(static tool => tool.ReadOnly).Select(static tool => tool.Name).ToArray());

        Assert.AreEqual("Description of click.", Tool("browser_click").Description);
        // A text that names another tool names it as it is called here, and what a page gives is said to be content.
        Assert.AreEqual("Gets a console message. Call browser_take_snapshot first. " + DesktopBrowserTools.ContentNote, Tool("browser_get_console_message").Description);
        StringAssert.Contains(Tool("browser_take_snapshot").Description, DesktopBrowserTools.ContentNote);
        StringAssert.Contains(Tool("browser_list_pages").Description, "the browser of this session");
        StringAssert.Contains(Tool("browser_close_page").Description, "The last page can be closed too.");
        StringAssert.Contains(Tool("browser_navigate_page").Description, "http or https");

        // A page is named by its place in the strip of the session.
        var click = Tool("browser_click").InputSchema.GetProperty("properties");
        StringAssert.Contains(click.GetProperty("pageId").GetProperty("description").GetString(), "from browser_list_pages");
        Assert.AreEqual("string", click.GetProperty("uid").GetProperty("type").GetString());
        // What means nothing here is not offered.
        var open = Tool("browser_new_page").InputSchema;
        CollectionAssert.AreEquivalent(new[] { "url", "timeout" }, open.GetProperty("properties").EnumerateObject().Select(static property => property.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "url" }, open.GetProperty("required").EnumerateArray().Select(static name => name.GetString()).ToArray());
        Assert.IsFalse(Tool("browser_select_page").InputSchema.GetProperty("properties").TryGetProperty("bringToFront", out _));
        StringAssert.Contains(Tool("browser_take_screenshot").InputSchema.GetProperty("properties").GetProperty("filePath").GetProperty("description").GetString(), "relative to the working folder of the caller");
    }

    [TestMethod]
    public async Task ASession_ListsAndAddressesThePagesOfItsOwnBrowser_ByTheirPlaceInItsStrip()
    {
        using var fixture = new Fixture();
        var (tools, ui, views) = (fixture.Tools, fixture.Ui, fixture.Views);

        // A session without a page is told how to get one.
        var empty = await fixture.CallAsync("a", "browser_take_snapshot");
        Assert.IsTrue(empty.IsError);
        Assert.AreEqual(DesktopBrowserTools.NoPage, empty.Text);
        Assert.AreEqual("The browser of this session has no page open.", (await fixture.CallAsync("a", "browser_list_pages")).Text);
        Assert.HasCount(0, ui.Calls);

        // A page is opened by the browsers, which the user sees, and loaded by the automation, which waits for its document.
        var first = await fixture.CallAsync("a", "browser_new_page", """{"url":"localhost:5173","timeout":2000}""");
        Assert.IsFalse(first.IsError, first.Text);
        Assert.AreEqual("Successfully navigated to http://localhost:5173/.\n## Pages\n1: Page of localhost (http://localhost:5173/) [selected]", first.Text);
        var pageA1 = fixture.Browsers.Peek("a")!.Pages[0].Id;
        Assert.AreEqual("navigate_page", ui.Calls[0].Name);
        AssertJson(new { type = "url", url = "http://localhost:5173/", pageId = ui.PageOf(pageA1), timeout = 2000 }, ui.Calls[0].Arguments);
        await fixture.CallAsync("a", "browser_new_page", """{"url":"https://example.com/docs"}""");
        await fixture.CallAsync("b", "browser_new_page", """{"url":"about:blank"}""");
        var pageA2 = fixture.Browsers.Peek("a")!.Pages[1].Id;
        var pageB1 = fixture.Browsers.Peek("b")!.Pages[0].Id;
        Assert.AreEqual(2, ui.Calls.Count, "the empty page has nothing to wait for");

        // Each session lists its own pages, numbered from 1, and no others.
        Assert.AreEqual("## Pages\n1: Page of localhost (http://localhost:5173/)\n2: Page of example.com (https://example.com/docs) [selected]", (await fixture.CallAsync("a", "browser_list_pages")).Text);
        Assert.AreEqual("## Pages\n1: about:blank [selected]", (await fixture.CallAsync("b", "browser_list_pages")).Text);

        // A tool acts on the page a call names, or on the page in front: the automation is given its own identifier of that page.
        ui.Calls.Clear();
        await fixture.CallAsync("a", "browser_click", """{"uid":"1_2","pageId":1,"dblClick":null}""");
        AssertJson(new { uid = "1_2", pageId = ui.PageOf(pageA1), dblClick = (string?)null }, ui.Calls[0].Arguments);
        await fixture.CallAsync("a", "browser_click", """{"uid":"1_2"}""");
        AssertJson(new { uid = "1_2", pageId = ui.PageOf(pageA2) }, ui.Calls[1].Arguments);
        await fixture.CallAsync("b", "browser_take_snapshot", "null");
        AssertJson(new { pageId = ui.PageOf(pageB1) }, ui.Calls[2].Arguments);
        Assert.AreSame(fixture.Files, ui.Calls[2].Files, "a file is saved where the session may write");

        // The number of another session's page is no page of this one, and neither is the identifier of the automation.
        ui.Calls.Clear();
        foreach (var number in new[] { 2, ui.PageOf(pageA1), 0, -1 })
        {
            var refused = await fixture.CallAsync("b", "browser_click", $$"""{"uid":"1_2","pageId":{{number}}}""");
            Assert.IsTrue(refused.IsError);
            Assert.AreEqual($"The browser of this session has no page {number}. Call browser_list_pages to list its pages.", refused.Text);
            Assert.IsTrue((await fixture.CallAsync("b", "browser_close_page", $$"""{"pageId":{{number}}}""")).IsError);
            Assert.IsTrue((await fixture.CallAsync("b", "browser_select_page", $$"""{"pageId":{{number}}}""")).IsError);
        }

        Assert.HasCount(0, ui.Calls);
        Assert.AreEqual(2, fixture.Browsers.Peek("a")!.Pages.Count);
        Assert.IsTrue((await fixture.CallAsync("b", "browser_click", """{"uid":"1_2","pageId":"1"}""")).IsError, "a page is named by a number");

        // A page is put in front and closed by its number; the last one can be closed too.
        var selected = await fixture.CallAsync("a", "browser_select_page", """{"pageId":1}""");
        Assert.AreEqual("## Pages\n1: Page of localhost (http://localhost:5173/) [selected]\n2: Page of example.com (https://example.com/docs)", selected.Text);
        Assert.AreEqual(pageA1, fixture.Browsers.Peek("a")!.ActivePageId);
        var closed = await fixture.CallAsync("a", "browser_close_page", """{"pageId":1}""");
        Assert.AreEqual("Closed page 1.\n## Pages\n1: Page of example.com (https://example.com/docs) [selected]", closed.Text);
        Assert.IsTrue(views.Of(pageA1).Disposed);
        Assert.AreEqual("Closed page 1.\nThe browser of this session has no page open.", (await fixture.CallAsync("a", "browser_close_page", """{"pageId":1}""")).Text);
        Assert.AreEqual(DesktopBrowserTools.NoPage, (await fixture.CallAsync("a", "browser_click", """{"uid":"1_2"}""")).Text);
        Assert.IsTrue((await fixture.CallAsync("a", "browser_select_page", "{}")).IsError);
        Assert.AreEqual(1, fixture.Browsers.Peek("b")!.Pages.Count, "the browser of the other session is as it was");

        // A caller that is no session has no browser, and a tool that is none is not run.
        Assert.IsTrue((await tools.CallAsync(null, "browser_list_pages", default, fixture.Files, CancellationToken.None)).IsError);
        StringAssert.Contains((await fixture.CallAsync("a", "take_snapshot")).Text, "Unknown tool");
        StringAssert.Contains((await fixture.CallAsync("a", "browser_resize_page")).Text, "Unknown tool");
        StringAssert.Contains((await fixture.CallAsync("a", "browser_list_pages", "[]")).Text, "must be a JSON object");
    }

    [TestMethod]
    public async Task TheBrowser_LoadsHttpAndHttpsOnly_WhoeverAsks()
    {
        using var fixture = new Fixture();
        var ui = fixture.Ui;

        foreach (var address in new[] { "file:///C:/Windows/win.ini", "app://codealta/index.html", "javascript://example.com/%0aalert(1)", "two words" })
        {
            var refused = await fixture.CallAsync("a", "browser_new_page", JsonSerializer.Serialize(new { url = address }));
            Assert.IsTrue(refused.IsError);
            Assert.AreEqual($"The browser of a session loads http and https addresses only. \"{address}\" is not one.", refused.Text);
        }

        Assert.AreEqual("The argument \"url\" is required.", (await fixture.CallAsync("a", "browser_new_page", "{}")).Text);
        Assert.AreEqual(0, fixture.Views.All.Count, "nothing is opened for an address that is refused");
        Assert.IsNull(fixture.Browsers.Peek("a"));

        await fixture.CallAsync("a", "browser_new_page", """{"url":"example.com"}""");
        ui.Calls.Clear();
        var navigation = await fixture.CallAsync("a", "browser_navigate_page", """{"type":"url","url":"file:///C:/x.html"}""");
        Assert.IsTrue(navigation.IsError);
        StringAssert.StartsWith(navigation.Text, DesktopBrowserTools.RefusedAddress);
        Assert.HasCount(0, ui.Calls);

        // What is typed without a scheme is read as the address field reads it, and the pages a navigation lists are those of the session.
        var loaded = await fixture.CallAsync("a", "browser_navigate_page", """{"url":"localhost:3000/app"}""");
        Assert.AreEqual("Successfully navigated to http://localhost:3000/app.\n## Pages\n1: Page of localhost (http://localhost:3000/app) [selected]", loaded.Text);
        AssertJson(new { url = "http://localhost:3000/app", pageId = ui.PageOf(fixture.Browsers.Peek("a")!.Pages[0].Id) }, ui.Calls[0].Arguments);
        await fixture.CallAsync("a", "browser_navigate_page", """{"type":"back"}""");
        AssertJson(new { type = "back", pageId = ui.PageOf(fixture.Browsers.Peek("a")!.Pages[0].Id) }, ui.Calls[1].Arguments);

        // A document that does not load leaves its page open, and the tool says so.
        ui.Answer = static (name, _) => name == "navigate_page" ? DesktopUiToolResult.Error("Navigation timed out.\n## Pages\n3: somebody else's (https://other.example/)") : null;
        var late = await fixture.CallAsync("a", "browser_new_page", """{"url":"slow.example"}""");
        Assert.IsTrue(late.IsError);
        Assert.AreEqual("Opened a page, and its document did not load: Navigation timed out.\n## Pages\n1: Page of localhost (http://localhost:3000/app)\n2: about:blank [selected]", late.Text);

        // A browser has so many pages and no more.
        ui.Answer = null;
        for (var count = 2; count < DesktopBrowsers.MaximumPages; count++) Assert.IsFalse((await fixture.CallAsync("a", "browser_new_page", """{"url":"about:blank"}""")).IsError);
        var full = await fixture.CallAsync("a", "browser_new_page", """{"url":"about:blank"}""");
        Assert.IsTrue(full.IsError);
        StringAssert.StartsWith(full.Text, "The browser of a session has 16 pages at most. Close one with browser_close_page.");

        // A system without browser views says so.
        using var none = new Fixture(new FakeViews { IsSupported = false });
        Assert.IsFalse(none.Tools.IsSupported);
        var unsupported = await none.CallAsync("a", "browser_list_pages");
        Assert.IsTrue(unsupported.IsError);
        StringAssert.Contains(unsupported.Text, "no browser for its sessions on this system");
    }

    [TestMethod]
    public async Task WhatTheAutomationAnswers_ReachesTheSession_WithItsOwnPagesAndTheNamesOfItsTools()
    {
        using var fixture = new Fixture();
        var ui = fixture.Ui;
        await fixture.CallAsync("a", "browser_new_page", """{"url":"example.com"}""");
        await fixture.CallAsync("b", "browser_new_page", """{"url":"other.example"}""");

        // The automation lists the pages of every session, by its own identifiers: the session reads its own.
        ui.Answer = static (_, _) => new DesktopUiToolResult(
            "Clicked.\nNote: the previously selected page was closed. Page 7 is now selected.\n## Pages\n7: Other (https://other.example/) [selected]\n9: Example (https://example.com/)\n## Latest page snapshot\nuid=1_0 RootWebArea \"take_snapshot\"\n## Pages\n1: text of the page",
            [new DesktopUiImage(new byte[] { 1 }, "image/png")], false);
        var clicked = await fixture.CallAsync("a", "browser_click", """{"uid":"1_0","includeSnapshot":true}""");
        Assert.AreEqual("Clicked.\n## Pages\n1: Page of example.com (https://example.com/) [selected]\n## Latest page snapshot\nuid=1_0 RootWebArea \"take_snapshot\"\n## Pages\n1: text of the page", clicked.Text);
        Assert.HasCount(1, clicked.Images);
        Assert.IsFalse(clicked.IsError);

        // What the automation says of its tools names them as they are called here; what a page gave stays as it is.
        ui.Answer = static (_, _) => DesktopUiToolResult.Error("No console message with msgid 4 was found. Call list_console_messages to list the messages.");
        Assert.AreEqual("No console message with msgid 4 was found. Call browser_list_console_messages to list the messages.", (await fixture.CallAsync("a", "browser_get_console_message", """{"msgid":4}""")).Text);
        ui.Answer = static (_, _) => new DesktopUiToolResult("Script ran on page and returned:\n\"call take_snapshot\"\n# Open dialog\nalert: hello.\nCall handle_dialog to handle it before continuing.", [], false);
        Assert.AreEqual("Script ran on page and returned:\n\"call take_snapshot\"\n# Open dialog\nalert: hello.\nCall browser_handle_dialog to handle it before continuing.", (await fixture.CallAsync("a", "browser_evaluate_script", """{"function":"() => 1"}""")).Text);

        // The automation does not know the view yet: nothing is asked of another page.
        ui.Answer = null;
        ui.Calls.Clear();
        ui.Knows = false;
        var early = await fixture.CallAsync("a", "browser_take_snapshot");
        Assert.IsTrue(early.IsError);
        Assert.HasCount(0, ui.Calls);
    }

    [TestMethod]
    public async Task APageOfASessionThatIsNotShown_IsDriven_AndCapturedOutOfSight()
    {
        var views = new FakeViews();
        using var fixture = new Fixture(views, liveViews: 2);
        var ui = fixture.Ui;
        await fixture.CallAsync("a", "browser_new_page", """{"url":"a.example"}""");
        var page = fixture.Browsers.Peek("a")!.Pages[0].Id;
        var view = views.Of(page);
        Assert.IsNull(view.Bounds, "the pane of the session shows no browser");

        // A hidden view gives no picture: it is shown past the edge of the window while the automation captures it.
        string[] during = [];
        ui.Answer = (name, _) =>
        {
            if (name == "take_screenshot") during = [.. view.Captures];
            return null;
        };
        var shot = await fixture.CallAsync("a", "browser_take_screenshot", """{"format":"jpeg"}""");
        Assert.IsFalse(shot.IsError, shot.Text);
        CollectionAssert.AreEqual(new[] { "begin hidden" }, during);
        CollectionAssert.AreEqual(new[] { "begin hidden", "end hidden" }, view.Captures);
        Assert.AreEqual("take_screenshot", ui.Calls[^1].Name);
        AssertJson(new { format = "jpeg", pageId = ui.PageOf(page) }, ui.Calls[^1].Arguments);

        // A snapshot needs no picture, and a view that is shown is captured where it is.
        await fixture.CallAsync("a", "browser_take_snapshot");
        Assert.HasCount(2, view.Captures);
        await fixture.Browsers.PlaceAsync("a", new BrowserBounds(600, 80, 500, 400));
        await fixture.CallAsync("a", "browser_take_screenshot");
        CollectionAssert.AreEqual(new[] { "begin hidden", "end hidden", "begin shown", "end shown" }, view.Captures);
        await fixture.Browsers.PlaceAsync("a", null);

        // A failure of the automation still hides the view again.
        ui.Answer = static (_, _) => throw new InvalidOperationException("the automation broke");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.CallAsync("a", "browser_take_screenshot"));
        Assert.AreEqual("end hidden", view.Captures[^1]);
        ui.Answer = null;

        // Views beyond the limit are let go: a page whose view is gone gets one again when its session drives it,
        // and the page a session drives is not the next one to lose its view.
        await fixture.CallAsync("b", "browser_new_page", """{"url":"b.example"}""");
        await fixture.CallAsync("a", "browser_take_snapshot");
        await fixture.CallAsync("c", "browser_new_page", """{"url":"c.example"}""");
        Assert.IsFalse(view.Disposed, "the page that was just driven keeps its view");
        var pageB = fixture.Browsers.Peek("b")!.Pages[0].Id;
        Assert.IsTrue(views.Of(pageB).Disposed);
        Assert.IsFalse(fixture.Browsers.Peek("b")!.Pages[0].Live);
        ui.Calls.Clear();
        Assert.IsFalse((await fixture.CallAsync("b", "browser_take_snapshot")).IsError);
        Assert.IsFalse(views.Of(pageB).Disposed);
        Assert.AreEqual("https://b.example/", views.Of(pageB).Address, "the new view loads the address of its page");
        AssertJson(new { pageId = ui.PageOf(pageB) }, ui.Calls.Single().Arguments);
    }

    [TestMethod]
    public async Task ASessionWhoseCommandsAreReviewed_ReadsItsPages_AndDoesNotActOnThem()
    {
        using var fixture = new Fixture();
        var ui = fixture.Ui;
        var reviewed = false;
        fixture.Sessions.Reviewed = sessionId => sessionId == "a" && Volatile.Read(ref reviewed);
        await fixture.CallAsync("a", "browser_new_page", """{"url":"example.com"}""");
        await fixture.CallAsync("a", "browser_new_page", """{"url":"example.org"}""");
        ui.Calls.Clear();

        // The mode of a session changes while it runs: the rule is the one of the moment of the call.
        Volatile.Write(ref reviewed, true);
        foreach (var (tool, arguments) in new[]
        {
            ("browser_click", """{"uid":"1_2"}"""), ("browser_evaluate_script", """{"function":"() => 1"}"""), ("browser_navigate_page", """{"type":"reload"}"""),
            ("browser_new_page", """{"url":"example.net"}"""), ("browser_close_page", """{"pageId":1}"""), ("browser_select_page", """{"pageId":1}"""),
        })
        {
            var refused = await fixture.CallAsync("a", tool, arguments);
            Assert.IsTrue(refused.IsError, tool);
            Assert.AreEqual(DesktopBrowserTools.ReviewedRefusal, refused.Text, tool);
        }

        Assert.HasCount(0, ui.Calls);
        var session = fixture.Browsers.Peek("a")!;
        Assert.AreEqual(2, session.Pages.Count);
        Assert.AreEqual(session.Pages[1].Id, session.ActivePageId);

        // Reading is what nobody has to review.
        Assert.IsFalse((await fixture.CallAsync("a", "browser_list_pages")).IsError);
        Assert.IsFalse((await fixture.CallAsync("a", "browser_take_snapshot", """{"pageId":1}""")).IsError);
        Assert.IsFalse((await fixture.CallAsync("a", "browser_take_screenshot")).IsError);
        Assert.IsFalse((await fixture.CallAsync("a", "browser_get_console_message", """{"msgid":1}""")).IsError);
        CollectionAssert.AreEqual(new[] { "take_snapshot", "take_screenshot", "get_console_message" }, ui.Calls.Select(static call => call.Name).ToArray());
        // A file in the folder the session works in is a change of that folder.
        var saved = await fixture.CallAsync("a", "browser_take_screenshot", """{"filePath":"shot.png"}""");
        Assert.IsTrue(saved.IsError);
        StringAssert.Contains(saved.Text, "does not save a file");
        Assert.HasCount(3, ui.Calls);

        // Another session is not concerned, and this one acts again when its commands are not reviewed any more.
        await fixture.CallAsync("b", "browser_new_page", """{"url":"example.com"}""");
        Assert.IsFalse((await fixture.CallAsync("b", "browser_click", """{"uid":"1_2"}""")).IsError);
        Volatile.Write(ref reviewed, false);
        Assert.IsFalse((await fixture.CallAsync("a", "browser_click", """{"uid":"1_2"}""")).IsError);
        Assert.IsFalse((await fixture.CallAsync("a", "browser_take_screenshot", """{"filePath":"shot.png"}""")).IsError);
    }

    private static void AssertJson(object expected, string actual)
    {
        using var left = JsonDocument.Parse(JsonSerializer.Serialize(expected));
        using var right = JsonDocument.Parse(actual);
        Assert.IsTrue(JsonElement.DeepEquals(left.RootElement, right.RootElement), actual);
    }

    /// <summary>The browser tools of a window that has fake views and a fake automation.</summary>
    internal sealed class Fixture : IDisposable
    {
        public Fixture(FakeViews? views = null, int liveViews = DesktopBrowsers.DefaultLiveViews)
        {
            Views = views ?? new FakeViews();
            Browsers = new DesktopBrowsers(Views, store: null, liveViews);
            Ui = new FakeBrowserUi(Root, Views);
            Tools = new DesktopBrowserTools(Ui, Browsers, Sessions, Ui.FindPage);
            Files = new DesktopUiFiles(Ui.FilesDirectory, [Ui.FilesDirectory]);
        }

        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-browser-tools-" + Guid.NewGuid().ToString("N"))).FullName;

        public FakeViews Views { get; }

        public DesktopBrowsers Browsers { get; }

        public FakeBrowserUi Ui { get; }

        public DesktopUiSessions Sessions { get; } = new();

        public DesktopBrowserTools Tools { get; }

        public DesktopUiFiles Files { get; }

        public Task<DesktopUiToolResult> CallAsync(string sessionId, string tool, string arguments = "{}")
        {
            using var document = JsonDocument.Parse(arguments);
            return Tools.CallAsync(sessionId, tool, document.RootElement.Clone(), Files, CancellationToken.None);
        }

        public void Dispose()
        {
            Browsers.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}

/// <summary>
/// The automation of the browser views of the tests: it has a few tools, knows the views by identifiers of its own,
/// records what is asked of it and answers what a test says. A navigation loads its address in the fake view.
/// </summary>
internal sealed class FakeBrowserUi : IDesktopUi
{
    private readonly FakeViews _views;
    private readonly Dictionary<string, int> _pages = new(StringComparer.Ordinal);

    public FakeBrowserUi(string root, FakeViews views)
    {
        _views = views;
        FilesDirectory = Directory.CreateDirectory(Path.Combine(root, "browser-tools")).FullName;
    }

    /// <summary>The tools of an automation, as NeoAstra describes them.</summary>
    public static IReadOnlyList<DesktopUiTool> Automation { get; } =
    [
        Tool("click", """{"type":"object","properties":{"pageId":{"type":"number","description":"Targets a specific page by ID."},"uid":{"type":"string"}},"required":["uid"]}"""),
        Tool("close_page", """{"type":"object","properties":{"pageId":{"type":"number","description":"The ID of the page to close. Call list_pages to list pages."}},"required":["pageId"]}"""),
        Tool("evaluate_script", """{"type":"object","properties":{"pageId":{"type":"number"},"function":{"type":"string"}}}"""),
        new("get_console_message", "Gets a console message. Call take_snapshot first.", Json("""{"type":"object","properties":{"pageId":{"type":"number"},"msgid":{"type":"number"}}}"""), true),
        Tool("handle_dialog", """{"type":"object","properties":{"pageId":{"type":"number"},"action":{"type":"string"}}}"""),
        Tool("list_console_messages", """{"type":"object","properties":{"pageId":{"type":"number"}}}""", readOnly: true),
        Tool("list_pages", readOnly: true),
        Tool("navigate_page", """{"type":"object","properties":{"pageId":{"type":"number"},"type":{"type":"string"},"url":{"type":"string"},"timeout":{"type":"integer"}}}"""),
        Tool("new_page", """{"type":"object","properties":{"url":{"type":"string"},"background":{"type":"boolean"},"isolatedContext":{"type":"string"},"timeout":{"type":"integer"}},"required":["url"]}"""),
        Tool("resize_page", """{"type":"object","properties":{"pageId":{"type":"number"},"width":{"type":"number"},"height":{"type":"number"}}}"""),
        Tool("select_page", """{"type":"object","properties":{"pageId":{"type":"number"},"bringToFront":{"type":"boolean"}},"required":["pageId"]}""", readOnly: true),
        Tool("take_screenshot", """{"type":"object","properties":{"pageId":{"type":"number"},"format":{"type":"string"},"filePath":{"type":"string"}}}"""),
        Tool("take_snapshot", """{"type":"object","properties":{"pageId":{"type":"number"},"verbose":{"type":"boolean"},"filePath":{"type":"string"}}}"""),
    ];

    public IReadOnlyList<DesktopUiTool> Tools => Automation;

    public string FilesDirectory { get; }

    public List<(string Name, string Arguments, DesktopUiFiles Files)> Calls { get; } = [];

    /// <summary>What a tool answers, or null for its usual answer.</summary>
    public Func<string, JsonElement, DesktopUiToolResult?>? Answer { get; set; }

    /// <summary>Whether the automation knows the views that exist.</summary>
    public bool Knows { get; set; } = true;

    /// <summary>The identifier the automation has for the view of a page: one for all sessions, and none of their numbers.</summary>
    public int? FindPage(string viewLabel)
    {
        if (!Knows || !viewLabel.StartsWith(WebViewBrowserViews.LabelPrefix, StringComparison.Ordinal)) return null;
        lock (_pages)
        {
            if (!_pages.TryGetValue(viewLabel, out var id)) _pages[viewLabel] = id = 101 + _pages.Count;
            return id;
        }
    }

    public int PageOf(string pageId) => FindPage(WebViewBrowserViews.LabelPrefix + pageId)!.Value;

    public Task<DesktopUiToolResult> CallAsync(string name, JsonElement arguments, DesktopUiFiles files, CancellationToken cancellationToken)
    {
        lock (Calls) Calls.Add((name, arguments.GetRawText(), files));
        if (Answer?.Invoke(name, arguments) is { } answer) return Task.FromResult(answer);
        if (name == "navigate_page" && arguments.TryGetProperty("url", out var url))
        {
            // The view loads the address, and says what it shows then.
            var address = new Uri(url.GetString()!);
            string label;
            lock (_pages) label = _pages.Single(page => page.Value == arguments.GetProperty("pageId").GetInt32()).Key;
            _views.Of(label[WebViewBrowserViews.LabelPrefix.Length..]).Report(new BrowserViewState(address.AbsoluteUri, "Page of " + address.Host, false, false, false));
            return Task.FromResult(new DesktopUiToolResult($"Successfully navigated to {address.AbsoluteUri}.\n## Pages\n1: the page of somebody else (https://other.example/) [selected]", [], false));
        }

        return Task.FromResult(new DesktopUiToolResult(name == "take_snapshot" ? "## Latest page snapshot\nuid=1_0 RootWebArea \"Page\"" : "done", [], false));
    }

    private static DesktopUiTool Tool(string name, string schema = """{"type":"object","properties":{}}""", bool readOnly = false)
        => new(name, $"Description of {name}.", Json(schema), readOnly);

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
