using CodeAlta.Desktop.Browser;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopBrowsersTests
{
    private const string Session = "01a1aaa3-0f76-722b-820e-491d20e5d5d8";
    private const string Main = "main";
    private const string Second = "window:2";

    [TestMethod]
    [DataRow("localhost:5173", "http://localhost:5173/")]
    [DataRow("localhost", "http://localhost/")]
    [DataRow("127.0.0.1:8080/app", "http://127.0.0.1:8080/app")]
    [DataRow("192.168.1.20", "http://192.168.1.20/")]
    [DataRow("devbox:3000", "http://devbox:3000/")]
    [DataRow("example.com", "https://example.com/")]
    [DataRow("  example.com/docs?q=1  ", "https://example.com/docs?q=1")]
    [DataRow("http://example.com", "http://example.com/")]
    [DataRow("https://example.com/a#b", "https://example.com/a#b")]
    [DataRow("about:blank", "about:blank")]
    public void WhatIsTypedAsAnAddress_IsAPageOfTheWeb_WithoutTlsOnlyForThisComputerAndItsNetwork(string typed, string expected)
        => Assert.AreEqual(expected, BrowserAddress.Parse(typed)?.AbsoluteUri);

    [TestMethod]
    [DataRow((string?)null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("two words")]
    [DataRow("file:///etc/hosts")]
    [DataRow("app://codealta/index.html")]
    [DataRow("javascript://example.com/%0aalert(1)")]
    [DataRow("data://text/html,x")]
    [DataRow("about://settings")]
    [DataRow("http://")]
    [DataRow("line\nbreak.example")]
    public void WhatIsNoPageOfTheWeb_IsNotLoaded(string? typed)
        => Assert.IsNull(BrowserAddress.Parse(typed));

    [TestMethod]
    public async Task OnlyTheBrowserTabInFront_IsShown_WhereTheWindowPutsIt()
    {
        var views = new FakeViews();
        await using var browsers = new DesktopBrowsers(views);
        var first = (await browsers.OpenAsync(Session, "localhost:5173")).PageId!;
        var second = (await browsers.OpenAsync(Session, "example.com")).PageId!;

        var placed = await browsers.PlaceAsync(Session, Main, Place(Main));

        Assert.AreEqual("ok", placed.Status);
        Assert.AreEqual(second, placed.Session!.ActivePageId);
        Assert.AreEqual(Main, placed.Session.Window);
        Assert.AreEqual(Place(Main), views.Of(second).Placement);
        Assert.IsNull(views.Of(first).Placement);
        Assert.AreEqual("http://localhost:5173/", views.Of(first).Address);

        await browsers.SelectAsync(Session, first);
        Assert.AreEqual(Place(Main), views.Of(first).Placement);
        Assert.IsNull(views.Of(second).Placement);
    }

    [TestMethod]
    public async Task TheBrowserOfASession_MovesToTheWindowThatShowsItNow_WhateverOrderTheTwoWindowsSpeakIn()
    {
        var views = new FakeViews();
        await using var browsers = new DesktopBrowsers(views);
        var page = (await browsers.OpenAsync(Session, "example.com")).PageId!;
        await browsers.PlaceAsync(Session, Main, Place(Main));

        // The tab goes to the second window: that window speaks first, then the main one says it no longer shows it.
        await browsers.PlaceAsync(Session, Second, Place(Second, 10));
        var after = await browsers.PlaceAsync(Session, Main, null);

        Assert.AreEqual(Place(Second, 10), views.Of(page).Placement);
        Assert.AreEqual(Second, after.Session!.Window);
        Assert.AreEqual(1, views.All.Count, "The same view moves: the page is not loaded again.");

        // And back, the other way round: the second window lets go first.
        await browsers.PlaceAsync(Session, Second, null);
        Assert.IsNull(views.Of(page).Placement);
        await browsers.PlaceAsync(Session, Main, Place(Main));
        Assert.AreEqual(Place(Main), views.Of(page).Placement);
        Assert.AreEqual(1, views.All.Count);
    }

    [TestMethod]
    public async Task APlacement_NamesTheWindowThatSpeaks_AndHasAnArea()
    {
        var views = new FakeViews();
        await using var browsers = new DesktopBrowsers(views);

        Assert.AreEqual("invalid", (await browsers.PlaceAsync(Session, Main, Place(Second))).Status);
        Assert.AreEqual("invalid", (await browsers.PlaceAsync(Session, Main, new BrowserPlacement(Main, 0, 0, 0, 10))).Status);
        Assert.AreEqual("invalid", (await browsers.PlaceAsync(Session, Main, new BrowserPlacement(Main, double.NaN, 0, 10, 10))).Status);
        Assert.AreEqual("invalid", (await browsers.PlaceAsync(Session, "", null)).Status);
        Assert.AreEqual("invalid", (await browsers.OpenAsync("", "example.com")).Status);
        Assert.AreEqual("invalid", (await browsers.OpenAsync(Session, "file:///etc/hosts")).Status);
    }

    [TestMethod]
    public async Task ClosingTheBrowserTabInFront_PutsTheOneBesideItInFront()
    {
        var views = new FakeViews();
        await using var browsers = new DesktopBrowsers(views);
        var first = (await browsers.OpenAsync(Session, "a.example")).PageId!;
        var second = (await browsers.OpenAsync(Session, "b.example")).PageId!;
        var third = (await browsers.OpenAsync(Session, "c.example")).PageId!;
        await browsers.SelectAsync(Session, second);

        var closed = await browsers.CloseAsync(Session, second);

        Assert.AreEqual(third, closed.Session!.ActivePageId);
        CollectionAssert.AreEqual(new[] { first, third }, closed.Session.Pages.Select(static page => page.Id).ToArray());
        Assert.IsTrue(views.Of(second).Disposed);
    }

    [TestMethod]
    public async Task TheBrowserTabsOfASession_AreKept_AndComeBackInTheNextRun()
    {
        var store = new MemoryStore();
        string active;
        await using (var browsers = new DesktopBrowsers(new FakeViews(), store))
        {
            await browsers.OpenAsync(Session, "localhost:5173");
            active = (await browsers.OpenAsync(Session, "example.com")).PageId!;
            await browsers.DisposeAsync();
        }

        var views = new FakeViews();
        await using var again = new DesktopBrowsers(views, store);
        var state = await again.GetAsync(Session);

        Assert.AreEqual(active, state.Session!.ActivePageId);
        CollectionAssert.AreEqual(new[] { "http://localhost:5173/", "https://example.com/" }, state.Session.Pages.Select(static page => page.Address).ToArray());
        Assert.AreEqual(0, views.All.Count, "A kept browser tab loads when it is shown, not before.");
        await again.PlaceAsync(Session, Main, Place(Main));
        Assert.AreEqual("https://example.com/", views.Of(active).Address);
    }

    [TestMethod]
    public async Task BeyondTheLiveViews_TheViewShownLongestAgoIsLetGo_NeverTheOneShown()
    {
        var views = new FakeViews();
        await using var browsers = new DesktopBrowsers(views, liveViews: 2);
        const string other = "other-session";
        var a = (await browsers.OpenAsync(Session, "a.example")).PageId!;
        await browsers.PlaceAsync(Session, Main, Place(Main));
        var b = (await browsers.OpenAsync(other, "b.example")).PageId!;
        var c = (await browsers.OpenAsync(other, "c.example")).PageId!;

        Assert.IsFalse(views.Of(a).Disposed, "The browser tab shown stays.");
        Assert.IsTrue(views.Of(b).Disposed);
        Assert.IsFalse(views.Of(c).Disposed);
        Assert.IsFalse((await browsers.GetAsync(other)).Session!.Pages.First(page => page.Id == b).Live);
    }

    [TestMethod]
    public async Task ADeletedSession_TakesItsBrowserWithIt()
    {
        var store = new MemoryStore();
        var views = new FakeViews();
        await using var browsers = new DesktopBrowsers(views, store);
        var page = (await browsers.OpenAsync(Session, "example.com")).PageId!;

        await browsers.ForgetAsync(Session);

        Assert.IsTrue(views.Of(page).Disposed);
        Assert.IsNull(browsers.Peek(Session));
        // What is kept goes away in the background: the end of the browsers waits for it.
        await browsers.DisposeAsync();
        Assert.IsTrue(store.Deleted.Contains(Session));
    }

    [TestMethod]
    public async Task WithoutBrowserViews_EveryRequestSaysUnsupported()
    {
        await using var browsers = new DesktopBrowsers(new FakeViews { IsSupported = false });

        Assert.AreEqual("unsupported", (await browsers.OpenAsync(Session, "example.com")).Status);
        Assert.AreEqual("unsupported", (await browsers.PlaceAsync(Session, Main, Place(Main))).Status);
    }

    [TestMethod]
    public async Task ADocumentThatAsksForANewWindow_OpensABrowserTabOfTheSameSession()
    {
        var views = new FakeViews();
        await using var browsers = new DesktopBrowsers(views);
        var page = (await browsers.OpenAsync(Session, "example.com")).PageId!;

        views.Of(page).OpenPage(new Uri("https://example.org/popup"));
        var state = await browsers.GetAsync(Session);

        Assert.AreEqual(2, state.Session!.Pages.Count);
        Assert.AreEqual("https://example.org/popup", state.Session.Pages[^1].Address);
    }

    [TestMethod]
    public async Task AJavaScriptDialogOfAPage_IsShownWithIt_UntilItIsAnswered()
    {
        var views = new FakeViews();
        await using var browsers = new DesktopBrowsers(views);
        var page = (await browsers.OpenAsync(Session, "example.com")).PageId!;
        var view = views.Of(page);

        view.Ask(new BrowserDialog("prompt", "Your name?", "Ada"));
        var asking = browsers.Peek(Session)!.Pages.Single();
        Assert.AreEqual(new BrowserDialog("prompt", "Your name?", "Ada"), asking.Dialog);
        Assert.AreEqual("https://example.com/", asking.Address);

        var answered = await browsers.AnswerDialogAsync(Session, page, true, "Grace");

        Assert.AreEqual("ok", answered.Status);
        Assert.AreEqual((true, "Grace"), view.Answers.Single());
        Assert.IsNull(answered.Session!.Pages.Single().Dialog);
        // A dialog that was answered meanwhile, by a tool of the session, is no error.
        Assert.AreEqual("ok", (await browsers.AnswerDialogAsync(Session, page, false, null)).Status);
        Assert.AreEqual("not_found", (await browsers.AnswerDialogAsync(Session, "no-such-page", true, null)).Status);
        Assert.AreEqual("invalid", (await browsers.AnswerDialogAsync(Session, page, true, new string('x', BrowserDialog.MaximumTextLength + 1))).Status);
    }

    [TestMethod]
    public async Task ABrowserTabWhoseViewIsLetGo_HasNoDialogLeft()
    {
        var views = new FakeViews();
        await using var browsers = new DesktopBrowsers(views, liveViews: 1);
        var first = (await browsers.OpenAsync(Session, "a.example")).PageId!;
        views.Of(first).Ask(new BrowserDialog("alert", "Hello", null));

        await browsers.OpenAsync(Session, "b.example");

        Assert.IsTrue(views.Of(first).Disposed);
        Assert.IsNull(browsers.Peek(Session)!.Pages.Single(page => page.Id == first).Dialog);
    }

    [TestMethod]
    [DataRow("http://127.0.0.1:2582/mcp", true)]
    [DataRow("http://localhost:2583/", true)]
    [DataRow("http://[::1]:2582/", true)]
    [DataRow("http://0.0.0.0:2582/", true)]
    [DataRow("http://app.localhost:2582/", true)]
    [DataRow("http://localhost:5173/", false)]
    [DataRow("http://192.168.1.20:2582/", false)]
    [DataRow("https://example.com:2582/", false)]
    public void ThePortsOfCodeAltaOnThisComputer_AreReachedByNoPage(string address, bool reserved)
        => Assert.AreEqual(reserved, BrowserAddress.IsReserved(new Uri(address), [2582, 2583]));

    private static BrowserPlacement Place(string window, double x = 0) => new(window, x, 40, 600, 700);

    private sealed class FakeViews : IBrowserViews
    {
        private readonly Lock _gate = new();
        private readonly List<FakeView> _all = [];

        public bool IsSupported { get; init; } = true;

        public IReadOnlyList<FakeView> All
        {
            get { lock (_gate) return [.. _all]; }
        }

        public FakeView Of(string pageId) => All.Last(view => view.PageId == pageId);

        public Task<T> RunAsync<T>(Func<Task<T>> body) => body();

        public ValueTask<IBrowserView> CreateAsync(string pageId, Action<Uri> openPage, CancellationToken cancellationToken)
        {
            var view = new FakeView(pageId, openPage);
            lock (_gate) _all.Add(view);
            return ValueTask.FromResult<IBrowserView>(view);
        }
    }

    private sealed class FakeView(string pageId, Action<Uri> openPage) : IBrowserView
    {
        public string PageId { get; } = pageId;

        public Action<Uri> OpenPage { get; } = openPage;

        public List<Uri> Navigations { get; } = [];

        public BrowserPlacement? Placement { get; private set; }

        public bool Disposed { get; private set; }

        public string Address => Navigations.Count == 0 ? BrowserAddress.Blank : Navigations[^1].AbsoluteUri;

        public BrowserViewState State => new(Address, string.Empty, false, false, false, Dialog);

        public BrowserDialog? Dialog { get; private set; }

        public List<(bool Accept, string? Text)> Answers { get; } = [];

        public IBrowserViewDriver? Driver => null;

        public event Action? Changed;

        /// <summary>The page opens a JavaScript dialog.</summary>
        public void Ask(BrowserDialog dialog)
        {
            Dialog = dialog;
            Changed?.Invoke();
        }

        public bool AnswerDialog(bool accept, string? text)
        {
            if (Dialog is null) return false;
            Dialog = null;
            Answers.Add((accept, text));
            Changed?.Invoke();
            return true;
        }

        public void Navigate(Uri address) => Navigations.Add(address);

        public void GoBack() { }

        public void GoForward() { }

        public void Reload() { }

        public void Stop() { }

        public void Place(BrowserPlacement? placement) => Placement = placement;

        public void Focus() { }

        public ValueTask<BrowserCapture?> CaptureAsync(CancellationToken cancellationToken) => ValueTask.FromResult<BrowserCapture?>(null);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryStore : IBrowserStore
    {
        private readonly Dictionary<string, BrowserSessionRecord> _records = new(StringComparer.Ordinal);

        public HashSet<string> Deleted { get; } = new(StringComparer.Ordinal);

        public BrowserSessionRecord? Load(string sessionId)
        {
            lock (_records) return _records.GetValueOrDefault(sessionId);
        }

        public void Save(string sessionId, BrowserSessionRecord record)
        {
            lock (_records) _records[sessionId] = record;
        }

        public void Delete(string sessionId)
        {
            lock (_records)
            {
                _records.Remove(sessionId);
                Deleted.Add(sessionId);
            }
        }
    }
}
