using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace CodeAlta.Desktop.Browser;

/// <summary>A page of the browser of a session, as the window and the commands see it.</summary>
/// <param name="Id">The identifier of the page.</param>
/// <param name="Address">The address of its document.</param>
/// <param name="Title">The title of its document; empty when it has none.</param>
/// <param name="Loading">Whether a document is being loaded.</param>
/// <param name="CanGoBack">Whether its history has an earlier document.</param>
/// <param name="CanGoForward">Whether its history has a later document.</param>
/// <param name="Live">Whether the page has its view: a page without one loads its address again when it is shown.</param>
/// <param name="Dialog">The JavaScript dialog the page has open and waits in, or null.</param>
internal sealed record BrowserPageInfo(string Id, string Address, string Title, bool Loading, bool CanGoBack, bool CanGoForward, bool Live, BrowserDialog? Dialog = null);

/// <summary>The browser of a session.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Pages">Its pages, in the order of their strip.</param>
/// <param name="ActivePageId">The page in front, or null when there is none.</param>
internal sealed record BrowserSessionInfo(string SessionId, IReadOnlyList<BrowserPageInfo> Pages, string? ActivePageId);

/// <summary>The outcome of a request to the browser of a session.</summary>
/// <param name="Status">
/// <c>ok</c>; <c>unsupported</c> on a system without browser views; <c>invalid</c> for an identifier, an address or a
/// place that cannot be taken; <c>not_found</c> when the session has no such page; <c>limit</c> when it has too many
/// pages; <c>failed</c> when the view of a page could not be created; <c>closed</c> while the application exits.
/// </param>
/// <param name="Session">The browser of the session after the request, when it was looked at.</param>
/// <param name="PageId">The page a request opened.</param>
internal sealed record BrowserResult(string Status, BrowserSessionInfo? Session = null, string? PageId = null);

/// <summary>What a page is asked to do besides loading an address.</summary>
internal enum BrowserCommand
{
    /// <summary>Go to the earlier document of the history.</summary>
    Back,
    /// <summary>Go to the later document of the history.</summary>
    Forward,
    /// <summary>Load the document again.</summary>
    Reload,
    /// <summary>Stop loading.</summary>
    Stop,
    /// <summary>Open the developer tools.</summary>
    DevTools,
    /// <summary>Take the keyboard.</summary>
    Focus,
}

/// <summary>
/// The browsers of the sessions: each session has pages of its own, one of them in front, shown where the pane of
/// the session puts it. A page is a native view that lies above the page of the application, so only the page in
/// front of a session whose pane says where it is has a visible view; every other one is hidden.
/// </summary>
/// <remarks>
/// <para>
/// The state belongs to the thread of the window: every request runs there, one piece at a time. What other
/// threads read is the description of each session as it was last published.
/// </para>
/// <para>
/// A page keeps its view while its session is not shown, so that it is found as it was left. Views cost memory:
/// beyond <see cref="LiveViews"/> the view that was shown, or driven by its session, longest ago is let go, and
/// its page loads its address again when it is next shown or driven.
/// </para>
/// </remarks>
internal sealed partial class DesktopBrowsers : IAsyncDisposable
{
    /// <summary>The most pages a session has.</summary>
    internal const int MaximumPages = 16;

    /// <summary>The most views that are kept by default.</summary>
    internal const int DefaultLiveViews = 12;

    /// <summary>The longest title that is kept, in UTF-16 units.</summary>
    internal const int MaximumTitleLength = 512;

    /// <summary>The longest identifier a request may carry, in UTF-16 units.</summary>
    internal const int MaximumIdLength = 128;

    private readonly IBrowserViews _views;
    private readonly IBrowserStore? _store;
    private readonly Dictionary<string, Task<Session>> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BrowserSessionInfo> _published = new(StringComparer.Ordinal);
    private readonly Lock _feedGate = new();
    private readonly ConcurrentDictionary<string, BrowserAnnotationsInfo> _publishedAnnotations = new(StringComparer.Ordinal);
    private readonly List<Channel<BrowserChange>> _feeds = [];
    private readonly Lock _savingGate = new();
    private Task _saving = Task.CompletedTask;
    private long _clock;
    private bool _closed;

    /// <summary>Creates the browsers over what creates their views.</summary>
    /// <param name="views">Creates the views, and runs work on their thread.</param>
    /// <param name="store">Keeps the pages of each session, or null to keep nothing.</param>
    /// <param name="liveViews">The most views that are kept; at least one.</param>
    /// <param name="annotations">Keeps the annotations of each session, or null to keep none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="views"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="liveViews"/> is not positive.</exception>
    internal DesktopBrowsers(IBrowserViews views, IBrowserStore? store = null, int liveViews = DefaultLiveViews, BrowserAnnotationStore? annotations = null)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(liveViews);
        (_views, _store, LiveViews, _annotationStore) = (views, store, liveViews, annotations);
    }

    /// <summary>Gets whether this system can show a browser inside the window.</summary>
    internal bool IsSupported => _views.IsSupported;

    /// <summary>Gets the most views that are kept.</summary>
    internal int LiveViews { get; }

    /// <summary>The browser of a session as it was last published, without reading what is kept for it.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>Its browser, or null when nothing asked for it in this run.</returns>
    internal BrowserSessionInfo? Peek(string sessionId) => sessionId is not null && _published.TryGetValue(sessionId, out var info) ? info : null;

    /// <summary>The browser of a session, with the pages kept for it from an earlier run.</summary>
    /// <param name="sessionId">The session.</param>
    internal Task<BrowserResult> GetAsync(string sessionId) => RunAsync(sessionId, static _ => Task.FromResult("ok"));

    /// <summary>Opens a page in the browser of a session and puts it in front.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="address">What was typed as its address; null or empty for the empty page.</param>
    internal async Task<BrowserResult> OpenAsync(string sessionId, string? address)
    {
        var target = string.IsNullOrWhiteSpace(address) ? new Uri(BrowserAddress.Blank) : BrowserAddress.Parse(address);
        if (target is null) return new("invalid");
        string? opened = null;
        var result = await RunAsync(sessionId, async session =>
        {
            if (session.Pages.Count >= MaximumPages) return "limit";
            var page = new Page(Guid.NewGuid().ToString("N"), session) { Address = target.AbsoluteUri };
            session.Pages.Add(page);
            session.Active = page;
            opened = page.Id;
            // The page loads at once, also in a session that is not shown.
            var view = await LiveAsync(page);
            await ApplyAsync(session);
            return view is null && !page.Closed ? "failed" : "ok";
        }).ConfigureAwait(false);
        return result with { PageId = opened };
    }

    /// <summary>Closes a page. The page beside it comes in front when it was the one in front.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="pageId">The page.</param>
    internal Task<BrowserResult> CloseAsync(string sessionId, string pageId) => RunAsync(sessionId, async session =>
    {
        if (Find(session, pageId) is not { } page) return "not_found";
        var index = session.Pages.IndexOf(page);
        session.Pages.RemoveAt(index);
        if (ReferenceEquals(session.Active, page)) session.Active = session.Pages.Count == 0 ? null : session.Pages[Math.Min(index, session.Pages.Count - 1)];
        Drop(page);
        await ApplyAsync(session);
        return "ok";
    });

    /// <summary>Puts a page in front.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="pageId">The page.</param>
    internal Task<BrowserResult> SelectAsync(string sessionId, string pageId) => RunAsync(sessionId, async session =>
    {
        if (Find(session, pageId) is not { } page) return "not_found";
        session.Active = page;
        await ApplyAsync(session);
        return "ok";
    });

    /// <summary>Moves a page to another place of the strip.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="pageId">The page.</param>
    /// <param name="index">Its new place, from 0; a place past the end is the last one.</param>
    internal Task<BrowserResult> MoveAsync(string sessionId, string pageId, int index) => RunAsync(sessionId, session =>
    {
        if (Find(session, pageId) is not { } page) return Task.FromResult("not_found");
        if (index < 0) return Task.FromResult("invalid");
        session.Pages.Remove(page);
        session.Pages.Insert(Math.Min(index, session.Pages.Count), page);
        return Task.FromResult("ok");
    });

    /// <summary>Loads an address in a page.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="pageId">The page.</param>
    /// <param name="address">What was typed as the address.</param>
    internal Task<BrowserResult> NavigateAsync(string sessionId, string pageId, string? address)
    {
        if (BrowserAddress.Parse(address) is not { } target) return Task.FromResult(new BrowserResult("invalid"));
        return RunAsync(sessionId, async session =>
        {
            if (Find(session, pageId) is not { } page) return "not_found";
            var created = page.View is null;
            (page.Address, page.Title) = (target.AbsoluteUri, string.Empty);
            if (await LiveAsync(page) is not { } view) return page.Closed ? "not_found" : "failed";
            // A view that was just created loads the address of its page by itself.
            if (!created) view.Navigate(target);
            return "ok";
        });
    }

    /// <summary>Asks a page to do something besides loading an address.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="pageId">The page.</param>
    /// <param name="command">What to do.</param>
    internal Task<BrowserResult> CommandAsync(string sessionId, string pageId, BrowserCommand command) => RunAsync(sessionId, async session =>
    {
        if (Find(session, pageId) is not { } page) return "not_found";
        // A page without its view has nothing to go back to: loading it again is all that it can be asked.
        if (page.View is not { } view) return command == BrowserCommand.Reload ? await LiveAsync(page) is null ? "failed" : "ok" : "ok";
        switch (command)
        {
            case BrowserCommand.Back: view.GoBack(); break;
            case BrowserCommand.Forward: view.GoForward(); break;
            case BrowserCommand.Reload: view.Reload(); break;
            case BrowserCommand.Stop: view.Stop(); break;
            case BrowserCommand.DevTools: view.OpenDevTools(); break;
            case BrowserCommand.Focus: view.Focus(); break;
            default: return "invalid";
        }

        return "ok";
    });

    /// <summary>
    /// Answers the JavaScript dialog a page has open. Whoever answers first is the one who answered: a dialog that
    /// a tool of the session answered meanwhile is gone, and the request is taken all the same.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="pageId">The page.</param>
    /// <param name="accept">Whether the dialog is accepted, or dismissed.</param>
    /// <param name="text">The text of a prompt that is accepted; null for its default text.</param>
    internal Task<BrowserResult> AnswerDialogAsync(string sessionId, string pageId, bool accept, string? text)
    {
        if (text is { Length: > BrowserDialog.MaximumTextLength }) return Task.FromResult(new BrowserResult("invalid"));
        return RunAsync(sessionId, async session =>
        {
            if (Find(session, pageId) is not { } page) return "not_found";
            if (page.View is { } view) await view.AnswerDialogAsync(accept, text);
            // What was answered is not shown any longer, whoever answered it.
            if (!page.Closed && page.View is { } current) page.Dialog = current.State.Dialog;
            return "ok";
        });
    }

    /// <summary>
    /// Says where the pane of a session shows its browser, or that it shows none: the page in front is shown
    /// there, and every other page of the session is hidden.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="bounds">The place in the window, or null when the browser of the session is not shown.</param>
    internal Task<BrowserResult> PlaceAsync(string sessionId, BrowserBounds? bounds)
    {
        if (bounds is { IsValid: false }) return Task.FromResult(new BrowserResult("invalid"));
        return RunAsync(sessionId, async session =>
        {
            session.Placement = bounds;
            await ApplyAsync(session);
            return "ok";
        });
    }

    /// <summary>Takes a picture of what a page shows.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="pageId">The page.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The picture, or null when there is no such page, it has no view, or it could not be captured.</returns>
    internal async Task<BrowserCapture?> CaptureAsync(string sessionId, string pageId, CancellationToken cancellationToken)
    {
        if (!_views.IsSupported || !IsIdentifier(sessionId) || !IsIdentifier(pageId)) return null;
        try
        {
            return await _views.RunAsync(async () =>
            {
                if (_closed || !_sessions.TryGetValue(sessionId, out var loading)) return null;
                var session = await loading;
                return Find(session, pageId) is { View: { } view } ? await view.CaptureAsync(cancellationToken) : null;
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes sure a page has its view, for who drives the page without showing it: a page whose view was let go
    /// gets a new one, which loads the address of the page. The page counts as used now, so its view is not the
    /// next one to be let go.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="pageId">The page.</param>
    internal Task<BrowserResult> EnsureLiveAsync(string sessionId, string pageId) => RunAsync(sessionId, async session =>
    {
        if (Find(session, pageId) is not { } page) return "not_found";
        if (await LiveAsync(page) is null) return page.Closed ? "not_found" : "failed";
        page.Shown = ++_clock;
        return "ok";
    });

    /// <summary>
    /// Runs work while a page can be captured: the view of a page that is hidden is shown past the edge of the
    /// window meanwhile, and hidden again afterwards. The work starts on the thread of the window.
    /// </summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="sessionId">The session.</param>
    /// <param name="pageId">The page.</param>
    /// <param name="body">The work, which is run also when there is no such page or it has no view.</param>
    /// <returns>What the work returned.</returns>
    /// <exception cref="ObjectDisposedException">The application is ending.</exception>
    internal Task<T> WhileCapturableAsync<T>(string sessionId, string pageId, Func<Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return _views.RunAsync(async () =>
        {
            IBrowserView? view = null;
            if (!_closed && sessionId is not null && _sessions.TryGetValue(sessionId, out var loading)) view = Find(await loading, pageId)?.View;
            view?.BeginCapture();
            try
            {
                return await body();
            }
            finally
            {
                view?.EndCapture();
            }
        });
    }

    /// <summary>
    /// Lets the views of a session go: its pages stay, with their addresses, and load again when they are shown.
    /// For a session the window no longer shows.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    internal Task<BrowserResult> ReleaseAsync(string sessionId) => RunAsync(sessionId, session =>
    {
        session.Placement = null;
        foreach (var page in session.Pages) Discard(page);
        return Task.FromResult("ok");
    }, known: true);

    /// <summary>Ends the browser of a session that is gone: its pages are closed and nothing is kept of them.</summary>
    /// <param name="sessionId">The session.</param>
    internal async Task ForgetAsync(string sessionId)
    {
        if (!IsIdentifier(sessionId)) return;
        try
        {
            await _views.RunAsync(async () =>
            {
                if (_sessions.Remove(sessionId, out var loading))
                {
                    var session = await loading;
                    foreach (var page in session.Pages) Drop(page);
                    session.Pages.Clear();
                    session.Active = null;
                    _published.TryRemove(sessionId, out _);
                    Tell(new BrowserSessionInfo(sessionId, [], null));
                    if (_publishedAnnotations.TryRemove(sessionId, out _)) Tell(new BrowserChange(null, new BrowserAnnotationsInfo(sessionId, [], null, null)));
                }

                Save(() => _store?.Delete(sessionId));
                return true;
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The application is ending: what is kept for the session is removed with it at a later cleanup.
        }
    }

    /// <summary>
    /// The browsers as they change: first each session that was asked for in this run, then a session each time
    /// its browser changes. A session whose browser ended is told once more, without pages.
    /// </summary>
    /// <param name="cancellationToken">Ends the watch.</param>
    internal async IAsyncEnumerable<BrowserSessionInfo> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var change in WatchChangesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (change.Session is { } session) yield return session;
        }
    }

    /// <summary>
    /// The browsers and the annotations as they change: first what each session that was asked for in this run
    /// has, then a session each time its pages or its annotations change.
    /// </summary>
    /// <param name="cancellationToken">Ends the watch.</param>
    internal async IAsyncEnumerable<BrowserChange> WatchChangesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<BrowserChange>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_feedGate)
        {
            if (_closed) channel.Writer.TryComplete();
            else _feeds.Add(channel);
        }

        try
        {
            foreach (var info in _published.Values) yield return new BrowserChange(info, null);
            foreach (var info in _publishedAnnotations.Values) yield return new BrowserChange(null, info);
            await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return change;
        }
        finally
        {
            lock (_feedGate) _feeds.Remove(channel);
        }
    }

    /// <summary>Ends every view and every watch, and waits for what is still being kept.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _views.RunAsync(async () =>
            {
                if (_closed) return true;
                _closed = true;
                foreach (var loading in _sessions.Values.ToArray())
                {
                    Session session;
                    try { session = await loading; }
                    catch (Exception exception) when (exception is not OutOfMemoryException) { continue; }
                    foreach (var page in session.Pages) Drop(page);
                }

                return true;
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The thread of the window is gone, and the views with it.
        }

        if (_pickClock is { } clock) await clock.DisposeAsync().ConfigureAwait(false);
        lock (_feedGate)
        {
            _closed = true;
            foreach (var feed in _feeds) feed.Writer.TryComplete();
            _feeds.Clear();
        }

        Task saving;
        lock (_savingGate) saving = _saving;
        await saving.ConfigureAwait(false);
    }

    // ---- On the thread of the window --------------------------------------------------------------------------------

    private sealed class Session(string id)
    {
        public string Id { get; } = id;
        public List<Page> Pages { get; } = [];
        public Page? Active { get; set; }
        public BrowserBounds? Placement { get; set; }
        public BrowserSessionRecord? Saved { get; set; }
        public List<BrowserAnnotation> Annotations { get; } = [];
        public int NextNumber { get; set; } = 1;
        public BrowserPendingPick? Pending { get; set; }
        // The pictures of annotations that were read or made in this run, by the identifier of their annotation.
        public Dictionary<string, BrowserCapture?> Pictures { get; } = new(StringComparer.Ordinal);
        public Marks? Marked { get; set; }
    }

    private sealed class Page(string id, Session session)
    {
        public string Id { get; } = id;
        public Session Session { get; } = session;
        public string Address { get; set; } = BrowserAddress.Blank;
        public string Title { get; set; } = string.Empty;
        public bool Loading { get; set; }
        public bool CanGoBack { get; set; }
        public bool CanGoForward { get; set; }
        public BrowserDialog? Dialog { get; set; }
        public IBrowserView? View { get; set; }
        public Action? Handler { get; set; }
        public Task? Creating { get; set; }
        public long Shown { get; set; }
        public bool Closed { get; set; }
        public bool Picking { get; set; }
        public (string Selector, string Address, int Rounds)? Reveal { get; set; }
    }

    private async Task<BrowserResult> RunAsync(string sessionId, Func<Session, Task<string>> body, bool known = false)
    {
        if (!_views.IsSupported) return new("unsupported");
        if (!IsIdentifier(sessionId)) return new("invalid");
        try
        {
            return await _views.RunAsync(async () =>
            {
                if (_closed) return new BrowserResult("closed");
                // A request that only lets go of what a session has does not read what is kept for it.
                if (known && !_sessions.ContainsKey(sessionId)) return new BrowserResult("ok");
                var session = await SessionAsync(sessionId);
                var status = await body(session);
                return new BrowserResult(status, Publish(session));
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return new("closed");
        }
    }

    private Task<Session> SessionAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var loading)) _sessions[sessionId] = loading = LoadAsync(sessionId);
        return loading;
    }

    private async Task<Session> LoadAsync(string sessionId)
    {
        var session = new Session(sessionId);
        // The annotations of the session are read with its pages, before anything uses the session.
        try { await Task.Run(() => LoadAnnotations(session)); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { session.Annotations.Clear(); }
        if (session.Annotations.Count > 0) StartClock();
        if (_store is not { } store) return session;
        BrowserSessionRecord? record;
        try { record = await Task.Run(() => store.Load(sessionId)); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { record = null; }
        if (record?.Pages is null) return session;
        foreach (var kept in record.Pages.Take(MaximumPages))
        {
            if (kept is null || !IsIdentifier(kept.Id) || session.Pages.Any(page => page.Id == kept.Id)) continue;
            if (!Uri.TryCreate(kept.Address, UriKind.Absolute, out var address) || !BrowserAddress.IsAllowed(address)) continue;
            session.Pages.Add(new Page(kept.Id, session) { Address = address.AbsoluteUri, Title = Shorten(kept.Title) });
        }

        session.Active = session.Pages.FirstOrDefault(page => page.Id == record.Active) ?? session.Pages.FirstOrDefault();
        session.Saved = Record(session);
        return session;
    }

    private static Page? Find(Session session, string? pageId) => pageId is null ? null : session.Pages.FirstOrDefault(page => page.Id == pageId);

    // The view of a page, created when the page has none.
    private async Task<IBrowserView?> LiveAsync(Page page)
    {
        if (page.View is null && !page.Closed)
        {
            var creating = page.Creating ??= CreateAsync(page);
            await creating;
            if (ReferenceEquals(page.Creating, creating)) page.Creating = null;
        }

        return page.View;
    }

    private async Task CreateAsync(Page page)
    {
        MakeRoom(page);
        IBrowserView view;
        try
        {
            view = await _views.CreateAsync(page.Id, address => _ = OpenAsync(page.Session.Id, address.AbsoluteUri), CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return; // The page stays without a view, and says so.
        }

        // The page was closed, or the application ended, while its view was created.
        if (page.Closed || _closed)
        {
            await view.DisposeAsync();
            return;
        }

        page.View = view;
        page.Handler = () => Changed(page);
        view.Changed += page.Handler;
        if (page.Address != BrowserAddress.Blank && Uri.TryCreate(page.Address, UriKind.Absolute, out var address)) view.Navigate(address);
    }

    // Lets the view go that was shown longest ago, when there are as many as are kept. A view that is shown stays.
    private void MakeRoom(Page arriving)
    {
        var live = new List<Page>();
        foreach (var loading in _sessions.Values)
        {
            if (loading.IsCompletedSuccessfully) live.AddRange(loading.Result.Pages.Where(static page => page.View is not null));
        }

        while (live.Count >= LiveViews)
        {
            var oldest = live.Where(page => !ReferenceEquals(page, arriving) && !(page.Session.Placement is not null && ReferenceEquals(page.Session.Active, page))).MinBy(static page => page.Shown);
            if (oldest is null) return;
            live.Remove(oldest);
            Discard(oldest);
            Publish(oldest.Session);
        }
    }

    // The page in front of a session is shown where its pane is; every other page of the session is hidden.
    private async Task ApplyAsync(Session session)
    {
        foreach (var page in session.Pages)
        {
            if (!ReferenceEquals(page, session.Active)) page.View?.Place(null);
        }

        if (session.Active is not { } active) return;
        if (session.Placement is null)
        {
            active.View?.Place(null);
            return;
        }

        var view = await LiveAsync(active);
        if (view is null) return;
        // The session may have another page in front, or no place any more, once the view exists.
        var shown = ReferenceEquals(session.Active, active) ? session.Placement : null;
        if (shown is not null) active.Shown = ++_clock;
        view.Place(shown);
    }

    // The view of a page reported a change.
    private void Changed(Page page)
    {
        if (page.View is not { } view || page.Closed) return;
        var state = view.State;
        // The empty page of a view that has not loaded its address yet is not what the page is about.
        if (Uri.TryCreate(state.Address, UriKind.Absolute, out var address) && BrowserAddress.IsAllowed(address)
            && !(address.AbsoluteUri == BrowserAddress.Blank && state.Loading)) page.Address = address.AbsoluteUri;
        (page.Title, page.Loading, page.CanGoBack, page.CanGoForward, page.Dialog) = (Shorten(state.Title), state.Loading, state.CanGoBack, state.CanGoForward, state.Dialog);
        Publish(page.Session);
    }

    // Lets the view of a page go, and keeps what it showed.
    private static void Discard(Page page)
    {
        if (page.View is not { } view) return;
        if (page.Handler is { } handler) view.Changed -= handler;
        (page.View, page.Handler, page.Loading, page.CanGoBack, page.CanGoForward, page.Dialog, page.Picking) = (null, null, false, false, false, null, false);
        _ = DisposeQuietlyAsync(view);
    }

    // Ends a page for good.
    private static void Drop(Page page)
    {
        page.Closed = true;
        Discard(page);
    }

    private static async Task DisposeQuietlyAsync(IBrowserView view)
    {
        try { await view.DisposeAsync(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A view that is going away with its window has nothing left to release.
        }
    }

    private BrowserSessionInfo Publish(Session session)
    {
        var info = new BrowserSessionInfo(session.Id,
            [.. session.Pages.Select(static page => new BrowserPageInfo(page.Id, page.Address, page.Title, page.Loading, page.CanGoBack, page.CanGoForward, page.View is not null, page.Dialog))],
            session.Active?.Id);
        // A session that was forgotten meanwhile is not brought back by a late change of one of its pages.
        if (!_sessions.ContainsKey(session.Id)) return info;
        if (_published.TryGetValue(session.Id, out var before) && Same(before, info)) return before;
        _published[session.Id] = info;
        Tell(info);
        var record = Record(session);
        if (_store is { } store && !Same(session.Saved, record))
        {
            session.Saved = record;
            Save(() => store.Save(session.Id, record));
        }

        return info;
    }

    private void Tell(BrowserSessionInfo info) => Tell(new BrowserChange(info, null));

    private void Tell(BrowserChange change)
    {
        lock (_feedGate)
        {
            foreach (var feed in _feeds) feed.Writer.TryWrite(change);
        }
    }

    // What is kept is written off the thread of the window, one write after the other.
    private void Save(Action write)
    {
        lock (_savingGate)
        {
            _saving = _saving.ContinueWith(_ =>
            {
                try { write(); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // The store keeps what it can: the browser works without it.
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private static BrowserSessionRecord Record(Session session)
        => new([.. session.Pages.Select(static page => new BrowserPageRecord(page.Id, page.Address, page.Title))], session.Active?.Id);

    private static bool Same(BrowserSessionRecord? left, BrowserSessionRecord right)
        => left is not null && left.Active == right.Active && left.Pages.SequenceEqual(right.Pages);

    private static bool Same(BrowserSessionInfo left, BrowserSessionInfo right)
        => left.ActivePageId == right.ActivePageId && left.Pages.SequenceEqual(right.Pages);

    private static string Shorten(string? title) => title is null ? string.Empty : title.Length <= MaximumTitleLength ? title : title[..MaximumTitleLength];

    private static bool IsIdentifier(string? value) => value is { Length: > 0 and <= MaximumIdLength } && !value.Any(char.IsControl);
}
