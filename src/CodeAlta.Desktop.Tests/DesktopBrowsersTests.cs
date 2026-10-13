using System.Text.Json;
using CodeAlta.Desktop.Browser;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopBrowsersTests
{
    private const string Epoch = "epoch-1";

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
    [DataRow("file:///C:/Windows/win.ini")]
    [DataRow("app://codealta/index.html")]
    [DataRow("javascript://example.com/%0aalert(1)")]
    [DataRow("data://text/html,x")]
    [DataRow("about://settings")]
    [DataRow("http://")]
    [DataRow("line\nbreak.example")]
    public void WhatIsNoPageOfTheWeb_IsNotLoaded(string? typed)
        => Assert.IsNull(BrowserAddress.Parse(typed));

    [TestMethod]
    public void TheBrowserLoads_HttpAndHttpsAndTheEmptyPage_AndNothingElse()
    {
        Assert.IsTrue(BrowserAddress.IsAllowed(new Uri("https://example.com/")));
        Assert.IsTrue(BrowserAddress.IsAllowed(new Uri("http://localhost:5173/")));
        Assert.IsTrue(BrowserAddress.IsAllowed(new Uri("about:blank")));
        Assert.IsFalse(BrowserAddress.IsAllowed(new Uri("file:///C:/x.html")));
        Assert.IsFalse(BrowserAddress.IsAllowed(new Uri("app://codealta/index.html")));
        Assert.IsFalse(BrowserAddress.IsAllowed(new Uri("javascript:alert(1)")));
        Assert.IsFalse(BrowserAddress.IsAllowed(new Uri("about:settings")));
        Assert.IsFalse(BrowserAddress.IsAllowed(new Uri("/relative", UriKind.Relative)));
        Assert.IsFalse(BrowserAddress.IsAllowed(null));
    }

    [TestMethod]
    public async Task APage_BelongsToItsSession_AndOnlyThePageInFrontOfAShownSessionIsShown()
    {
        var views = new FakeViews();
        await using var browsers = new DesktopBrowsers(views);

        var first = await browsers.OpenAsync("session-a", "localhost:5173");
        Assert.AreEqual("ok", first.Status);
        var second = await browsers.OpenAsync("session-a", "example.com");
        var other = await browsers.OpenAsync("session-b", null);
        Assert.AreEqual(2, second.Session!.Pages.Count);
        Assert.AreEqual(second.PageId, second.Session.ActivePageId, "a page that is opened comes in front");
        Assert.AreEqual(1, other.Session!.Pages.Count, "a session has the pages it opened, and no others");
        Assert.AreEqual("about:blank", other.Session.Pages[0].Address);
        Assert.AreEqual("http://localhost:5173/", views.Of(first.PageId!).Address, "a page loads its address at once, shown or not");

        // Nothing is shown before the pane of a session says where its browser is.
        Assert.IsTrue(views.All.All(static view => view.Bounds is null));

        var place = new BrowserBounds(600, 80, 500, 400);
        Assert.AreEqual("ok", (await browsers.PlaceAsync("session-a", place)).Status);
        Assert.AreEqual(place, views.Of(second.PageId!).Bounds);
        Assert.IsNull(views.Of(first.PageId!).Bounds, "the page behind is hidden");
        Assert.IsNull(views.Of(other.PageId!).Bounds, "so is the browser of a session that is not shown");

        Assert.AreEqual("ok", (await browsers.SelectAsync("session-a", first.PageId!)).Status);
        Assert.AreEqual(place, views.Of(first.PageId!).Bounds);
        Assert.IsNull(views.Of(second.PageId!).Bounds);

        // Another session in front: its browser is shown, this one is hidden and stays as it was.
        await browsers.PlaceAsync("session-a", null);
        await browsers.PlaceAsync("session-b", place);
        Assert.IsNull(views.Of(first.PageId!).Bounds);
        Assert.AreEqual(place, views.Of(other.PageId!).Bounds);
        Assert.IsFalse(views.Of(first.PageId!).Disposed, "a page keeps its view while its session is not shown");

        await browsers.PlaceAsync("session-b", null);
        await browsers.PlaceAsync("session-a", place);
        Assert.AreEqual(place, views.Of(first.PageId!).Bounds, "back in the session, its browser is there again");
        Assert.AreEqual(1, views.Of(first.PageId!).Navigations.Count, "and was not loaded again");
    }

    [TestMethod]
    public async Task APageThatIsClosed_EndsItsView_AndThePageBesideItComesInFront()
    {
        var views = new FakeViews();
        await using var browsers = new DesktopBrowsers(views);
        var one = (await browsers.OpenAsync("s", "one.example")).PageId!;
        var two = (await browsers.OpenAsync("s", "two.example")).PageId!;
        var three = (await browsers.OpenAsync("s", "three.example")).PageId!;
        await browsers.PlaceAsync("s", new BrowserBounds(0, 0, 100, 100));

        var closed = await browsers.CloseAsync("s", three);
        Assert.AreEqual("ok", closed.Status);
        Assert.AreEqual(two, closed.Session!.ActivePageId);
        Assert.IsTrue(views.Of(three).Disposed);
        Assert.IsNotNull(views.Of(two).Bounds);

        Assert.AreEqual("not_found", (await browsers.CloseAsync("s", three)).Status);
        Assert.AreEqual("not_found", (await browsers.SelectAsync("s", "no-such-page")).Status);
        Assert.AreEqual("not_found", (await browsers.CloseAsync("another-session", one)).Status, "a page is reached through its own session only");

        var moved = await browsers.MoveAsync("s", two, 0);
        CollectionAssert.AreEqual(new[] { two, one }, moved.Session!.Pages.Select(static page => page.Id).ToArray());
        await browsers.CloseAsync("s", two);
        var last = await browsers.CloseAsync("s", one);
        Assert.AreEqual(0, last.Session!.Pages.Count);
        Assert.IsNull(last.Session.ActivePageId);
    }

    [TestMethod]
    public async Task Requests_AreRefused_ForWhatCannotBeTaken()
    {
        var views = new FakeViews();
        await using var browsers = new DesktopBrowsers(views);
        Assert.AreEqual("invalid", (await browsers.OpenAsync("s", "file:///C:/x.html")).Status);
        Assert.AreEqual("invalid", (await browsers.OpenAsync("", "example.com")).Status);
        Assert.AreEqual("invalid", (await browsers.GetAsync(new string('x', DesktopBrowsers.MaximumIdLength + 1))).Status);
        Assert.AreEqual("invalid", (await browsers.PlaceAsync("s", new BrowserBounds(0, 0, 0, 10))).Status);
        Assert.AreEqual(0, views.All.Count, "nothing was created for a request that was refused");

        for (var index = 0; index < DesktopBrowsers.MaximumPages; index++) Assert.AreEqual("ok", (await browsers.OpenAsync("s", null)).Status);
        Assert.AreEqual("limit", (await browsers.OpenAsync("s", null)).Status);

        var page = (await browsers.GetAsync("s")).Session!.Pages[0].Id;
        Assert.AreEqual("invalid", (await browsers.NavigateAsync("s", page, "app://codealta/")).Status);
        Assert.AreEqual("ok", (await browsers.NavigateAsync("s", page, "example.com/next")).Status);
        Assert.AreEqual("https://example.com/next", views.Of(page).Address);

        var unsupported = new DesktopBrowsers(new FakeViews { IsSupported = false });
        Assert.AreEqual("unsupported", (await unsupported.OpenAsync("s", "example.com")).Status);
        Assert.AreEqual("unsupported", (await unsupported.GetAsync("s")).Status);
    }

    [TestMethod]
    public async Task WhatAViewReports_IsWhatItsPageShows_AndTheWatchTellsIt()
    {
        var views = new FakeViews();
        await using var browsers = new DesktopBrowsers(views);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var watch = browsers.WatchAsync(stop.Token).GetAsyncEnumerator(stop.Token);
        var next = watch.MoveNextAsync();

        var page = (await browsers.OpenAsync("s", "example.com")).PageId!;
        Assert.IsTrue(await next);
        Assert.AreEqual("s", watch.Current.SessionId);

        views.Of(page).Report(new BrowserViewState("https://example.com/landing", "Example", false, true, false));
        BrowserPageInfo shown;
        do
        {
            Assert.IsTrue(await watch.MoveNextAsync());
            shown = watch.Current.Pages[0];
        }
        while (shown.Title != "Example");
        Assert.AreEqual("https://example.com/landing", shown.Address);
        Assert.IsTrue(shown.CanGoBack);
        Assert.IsTrue(shown.Live);
        Assert.AreEqual(shown, browsers.Peek("s")!.Pages[0]);

        // A document that asks for a new window gets a new page of the same session.
        views.Of(page).OpenPage(new Uri("https://example.com/popup"));
        var session = await Until(browsers, "s", static info => info.Pages.Count == 2);
        Assert.AreEqual("https://example.com/popup", session.Pages[1].Address);
        Assert.AreEqual(session.Pages[1].Id, session.ActivePageId);

        foreach (var command in Enum.GetValues<BrowserCommand>()) Assert.AreEqual("ok", (await browsers.CommandAsync("s", page, command)).Status);
        CollectionAssert.AreEqual(new[] { "back", "forward", "reload", "stop", "devtools", "focus" }, views.Of(page).Commands);
    }

    [TestMethod]
    public async Task ViewsBeyondTheLimit_AreLetGo_TheOneShownLongestAgoFirst_AndLoadAgainWhenShown()
    {
        var views = new FakeViews();
        await using var browsers = new DesktopBrowsers(views, store: null, liveViews: 2);
        var place = new BrowserBounds(0, 0, 300, 200);
        var a = (await browsers.OpenAsync("a", "a.example")).PageId!;
        await browsers.PlaceAsync("a", place);
        await browsers.PlaceAsync("a", null);
        var b = (await browsers.OpenAsync("b", "b.example")).PageId!;
        await browsers.PlaceAsync("b", place);
        await browsers.PlaceAsync("b", null);

        // A third view: the one of the session that was shown longest ago goes, and its page stays.
        var c = (await browsers.OpenAsync("c", "c.example")).PageId!;
        await browsers.PlaceAsync("c", place);
        Assert.IsTrue(views.Of(a).Disposed);
        Assert.IsFalse(views.Of(b).Disposed);
        var kept = browsers.Peek("a")!.Pages.Single();
        Assert.IsFalse(kept.Live);
        Assert.AreEqual("https://a.example/", kept.Address);

        // Shown again, the page gets a new view and loads its address. The view that is shown is never the one let go.
        await browsers.PlaceAsync("a", place);
        var again = views.All.Last(view => view.PageId == a);
        Assert.IsFalse(again.Disposed);
        Assert.AreEqual("https://a.example/", again.Address);
        Assert.AreEqual(place, again.Bounds);
        Assert.IsFalse(views.Of(c).Disposed, "a view that is shown stays");
        Assert.IsTrue(views.Of(b).Disposed);

        // The window stopped showing a session: its views go, its pages stay.
        Assert.AreEqual("ok", (await browsers.ReleaseAsync("c")).Status);
        Assert.IsTrue(views.Of(c).Disposed);
        Assert.IsFalse(browsers.Peek("c")!.Pages.Single().Live);
        Assert.AreEqual("ok", (await browsers.ReleaseAsync("never-opened")).Status);
        Assert.IsNull(browsers.Peek("never-opened"), "letting go reads nothing for a session that has no browser");
    }

    [TestMethod]
    public async Task ThePagesOfASession_AreKeptWithIt_AndGoWithIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "alta-browser-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BrowserFileStore(root);
            string first, second;
            var views = new FakeViews();
            await using (var browsers = new DesktopBrowsers(views, store))
            {
                first = (await browsers.OpenAsync("01a13556-ef31", "localhost:5173")).PageId!;
                second = (await browsers.OpenAsync("01a13556-ef31", "example.com")).PageId!;
                views.Of(second).Report(new BrowserViewState("https://example.com/", "Example Domain", false, false, false));
                await browsers.SelectAsync("01a13556-ef31", first);
            }

            // The next run: the pages are there, without views until the session is shown.
            var restarted = new FakeViews();
            await using (var browsers = new DesktopBrowsers(restarted, store))
            {
                var session = (await browsers.GetAsync("01a13556-ef31")).Session!;
                CollectionAssert.AreEqual(new[] { first, second }, session.Pages.Select(static page => page.Id).ToArray());
                Assert.AreEqual(first, session.ActivePageId);
                Assert.AreEqual("Example Domain", session.Pages[1].Title);
                Assert.IsTrue(session.Pages.All(static page => !page.Live));
                Assert.AreEqual(0, restarted.All.Count);

                await browsers.PlaceAsync("01a13556-ef31", new BrowserBounds(0, 0, 400, 300));
                Assert.AreEqual("http://localhost:5173/", restarted.Of(first).Address, "the page in front loads its address when it is shown");
                Assert.AreEqual(1, restarted.All.Count, "the page behind waits until it is put in front");

                // The session was deleted: its pages are closed and nothing is kept of them.
                await browsers.ForgetAsync("01a13556-ef31");
                Assert.IsTrue(restarted.Of(first).Disposed);
                Assert.IsNull(browsers.Peek("01a13556-ef31"));
            }

            Assert.IsNull(store.Load("01a13556-ef31"));
            Assert.IsFalse(Directory.Exists(store.Folder("01a13556-ef31")));

            // An identifier that cannot name a folder keeps nothing, and reads nothing from elsewhere.
            Assert.IsNull(store.Folder(".."));
            Assert.IsNull(store.Folder("a/b"));
            Assert.IsNull(store.Folder("a\\b"));
            store.Save("..", new BrowserSessionRecord([new BrowserPageRecord("p", "https://example.com/", "")], "p"));
            Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "pages.json")));

            // A file that is not what was written is as if it were not there, and a page that the browser does not load is left out.
            Directory.CreateDirectory(store.Folder("broken")!);
            File.WriteAllText(Path.Combine(store.Folder("broken")!, "pages.json"), "{ not json");
            Assert.IsNull(store.Load("broken"));
            store.Save("mixed", new BrowserSessionRecord([new BrowserPageRecord("p1", "file:///C:/x.html", "x"), new BrowserPageRecord("p2", "https://example.com/", "y")], "p1"));
            await using var mixed = new DesktopBrowsers(new FakeViews(), store);
            var loaded = (await mixed.GetAsync("mixed")).Session!;
            Assert.AreEqual("p2", loaded.Pages.Single().Id);
            Assert.AreEqual("p2", loaded.ActivePageId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AViewThatCannotBeCreated_LeavesItsPageWithoutOne_AndSaysSo()
    {
        var views = new FakeViews { Fail = true };
        await using var browsers = new DesktopBrowsers(views);
        var opened = await browsers.OpenAsync("s", "example.com");
        Assert.AreEqual("failed", opened.Status);
        Assert.IsFalse(opened.Session!.Pages.Single().Live);

        // The next request tries again.
        views.Fail = false;
        Assert.AreEqual("ok", (await browsers.CommandAsync("s", opened.PageId!, BrowserCommand.Reload)).Status);
        Assert.IsTrue(browsers.Peek("s")!.Pages.Single().Live);
        Assert.IsNull(await browsers.CaptureAsync("s", "no-such-page", CancellationToken.None));
        Assert.IsNotNull(await browsers.CaptureAsync("s", opened.PageId!, CancellationToken.None));
    }

    [TestMethod]
    public async Task WithoutAnOwnedHost_EveryOperationIsUnavailable_AndAnotherEpochIsRefused()
    {
        var unavailable = new BrowserService();
        Assert.AreEqual("unavailable", (await unavailable.StateAsync(new(Epoch, "s"))).Status);
        Assert.AreEqual("unavailable", (await unavailable.OpenAsync(new(Epoch, "s", "example.com"))).Status);
        Assert.AreEqual("unavailable", (await unavailable.PlaceAsync(new(Epoch, "s", true, 0, 0, 10, 10))).Status);
        Assert.AreEqual("unavailable", (await unavailable.CaptureAsync(new(Epoch, "s", "p"), CancellationToken.None)).Status);
        Assert.IsFalse(await unavailable.WatchAsync(new(Epoch), CancellationToken.None).GetAsyncEnumerator().MoveNextAsync());

        var views = new FakeViews();
        await using var browsers = new DesktopBrowsers(views);
        var service = new BrowserService(browsers, Epoch);
        Assert.AreEqual("stale_epoch", (await service.OpenAsync(new("another", "s", "example.com"))).Status);
        Assert.AreEqual("stale_epoch", (await service.CommandAsync(new("another", "s", "p", "reload"))).Status);
        Assert.AreEqual(0, views.All.Count);
        Assert.IsFalse(await service.WatchAsync(new("another"), CancellationToken.None).GetAsyncEnumerator().MoveNextAsync());
    }

    [TestMethod]
    public async Task ThePage_OpensPlacesAndDrivesTheBrowserOfASession_OverTheService()
    {
        var views = new FakeViews();
        await using var browsers = new DesktopBrowsers(views);
        var service = new BrowserService(browsers, Epoch);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var watch = service.WatchAsync(new(Epoch), stop.Token).GetAsyncEnumerator(stop.Token);
        Assert.IsTrue(await watch.MoveNextAsync());
        Assert.AreEqual("ready", watch.Current.Kind);
        Assert.IsTrue(watch.Current.Supported);

        var opened = await service.OpenAsync(new(Epoch, "s", "localhost:3000"));
        Assert.AreEqual("ok", opened.Status);
        Assert.AreEqual("http://localhost:3000/", opened.Session!.Pages.Single().Address);
        Assert.IsTrue(await watch.MoveNextAsync());
        Assert.AreEqual("session", watch.Current.Kind);
        Assert.AreEqual("s", watch.Current.Session!.SessionId);

        Assert.AreEqual("ok", (await service.PlaceAsync(new(Epoch, "s", true, 640, 96, 512, 384))).Status);
        Assert.AreEqual(new BrowserBounds(640, 96, 512, 384), views.Of(opened.PageId!).Bounds);
        Assert.AreEqual("invalid", (await service.PlaceAsync(new(Epoch, "s", true, 0, 0, -1, 10))).Status);
        Assert.AreEqual("ok", (await service.PlaceAsync(new(Epoch, "s", false, 0, 0, 0, 0))).Status, "a browser that is not shown has no place to check");
        Assert.IsNull(views.Of(opened.PageId!).Bounds);

        Assert.AreEqual("ok", (await service.CommandAsync(new(Epoch, "s", opened.PageId, "reload"))).Status);
        Assert.AreEqual("invalid", (await service.CommandAsync(new(Epoch, "s", opened.PageId, "erase"))).Status);
        Assert.AreEqual("ok", (await service.NavigateAsync(new(Epoch, "s", opened.PageId, "example.com"))).Status);
        Assert.AreEqual("invalid", (await service.NavigateAsync(new(Epoch, "s", opened.PageId, "file:///C:/x"))).Status);
        Assert.AreEqual("not_found", (await service.SelectAsync(new(Epoch, "s", null))).Status);

        var capture = await service.CaptureAsync(new(Epoch, "s", opened.PageId), CancellationToken.None);
        Assert.AreEqual("ok", capture.Status);
        StringAssert.StartsWith(capture.Image, "data:image/jpeg;base64,");
        Assert.AreEqual("unavailable_page", (await service.CaptureAsync(new(Epoch, "s", "missing"), CancellationToken.None)).Status);

        // The names the page reads are those of the contract.
        var json = JsonSerializer.Serialize(new BrowserEvent("session", true, opened.Session), DesktopJsonContext.Default.BrowserEvent);
        StringAssert.Contains(json, "\"kind\":\"session\"");
        StringAssert.Contains(json, "\"activePageId\":");
        StringAssert.Contains(json, "\"canGoBack\":false");

        Assert.AreEqual("ok", (await service.CloseAsync(new(Epoch, "s", opened.PageId))).Status);
        Assert.AreEqual("ok", (await service.ReleaseAsync(new(Epoch, "s"))).Status);
    }

    private static async Task<BrowserSessionInfo> Until(DesktopBrowsers browsers, string sessionId, Func<BrowserSessionInfo, bool> condition)
    {
        var patience = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < patience)
        {
            if (browsers.Peek(sessionId) is { } info && condition(info)) return info;
            await Task.Delay(10);
        }

        Assert.Fail("The browser of the session did not get there.");
        return null!;
    }

    private sealed class FakeViews : IBrowserViews
    {
        private readonly Lock _gate = new();
        private readonly List<FakeView> _all = [];

        public bool IsSupported { get; init; } = true;

        public bool Fail { get; set; }

        public IReadOnlyList<FakeView> All
        {
            get { lock (_gate) return [.. _all]; }
        }

        /// <summary>The view a page has now: the last one that was created for it.</summary>
        public FakeView Of(string pageId) => All.Last(view => view.PageId == pageId);

        public Task<T> RunAsync<T>(Func<Task<T>> body) => body();

        public ValueTask<IBrowserView> CreateAsync(string pageId, Action<Uri> openPage, CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("No browser here.");
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

        public List<string> Commands { get; } = [];

        public BrowserBounds? Bounds { get; private set; }

        public bool Disposed { get; private set; }

        public string Address => Navigations.Count == 0 ? BrowserAddress.Blank : Navigations[^1].AbsoluteUri;

        public BrowserViewState State { get; private set; } = new(BrowserAddress.Blank, string.Empty, false, false, false);

        public event Action? Changed;

        public void Report(BrowserViewState state)
        {
            State = state;
            Changed?.Invoke();
        }

        public void Navigate(Uri address) => Navigations.Add(address);

        public void GoBack() => Commands.Add("back");

        public void GoForward() => Commands.Add("forward");

        public void Reload() => Commands.Add("reload");

        public void Stop() => Commands.Add("stop");

        public void OpenDevTools() => Commands.Add("devtools");

        public void Focus() => Commands.Add("focus");

        public void Place(BrowserBounds? bounds) => Bounds = bounds;

        public ValueTask<BrowserCapture?> CaptureAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<BrowserCapture?>(new BrowserCapture(new byte[] { 0xff, 0xd8, 0xff, 0xd9 }, "image/jpeg", 2, 1));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Bounds = null;
            return ValueTask.CompletedTask;
        }
    }
}
