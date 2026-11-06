using System.Runtime.Versioning;
using CodeAlta.Browser.Discovery;
using CodeAlta.Browser.Drivers;
using CodeAlta.Browser.Tools;
using CodeAlta.Plugin.Browser;

namespace CodeAlta.Desktop.Browser.WebKit;

/// <summary>
/// The browser built into the application, as a browser the tools of an agent drive: its contexts are the browsers of
/// the sessions, whose tabs the user sees beside them. There is no process to start: the views are the application's.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class WebKitBrowserConnection(DesktopBrowsers browsers) : IBrowserConnection
{
    /// <summary>The browser built into the application, as the browser choice names it.</summary>
    internal static InstalledBrowser Installed { get; } = new(BrowserPluginHost.EmbeddedId, "CodeAlta (built-in)", BrowserEngine.Embedded, string.Empty, null, null, []);

    /// <inheritdoc />
    public InstalledBrowser Browser => Installed;

    /// <inheritdoc />
    public bool IsClosed => false;

    /// <inheritdoc />
    public async Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.SessionId)) throw new BrowserToolException("no-session", "The built-in browser shows the tabs of a session: the session is missing.");
        // The views of the application share one network: they cannot go through the proxy of a sandbox.
        if (options.Proxy is not null)
        {
            throw new BrowserToolException("sandbox", "A sandboxed session cannot use the built-in browser, which cannot keep to the network of its sandbox: name Chrome, Edge or Firefox instead (alta browser activate --browser chrome).");
        }

        // The tabs the session has, kept from an earlier run too, are the agent's first: it does not open an empty one beside them.
        var state = await browsers.GetAsync(options.SessionId).ConfigureAwait(false);
        if (state.Status == "unsupported") throw new BrowserToolException("not-supported", "The built-in browser is only in CodeAlta Desktop on macOS.");
        return new WebKitBrowserContext(browsers, options.SessionId);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// The browser tabs of a session in the browser built into the application. They are the user's as much as the
/// agent's: ending the context of the agent leaves them open.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class WebKitBrowserContext : IBrowserContext
{
    private readonly DesktopBrowsers _browsers;
    private readonly string _sessionId;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, WebKitPage> _pages = new(StringComparer.Ordinal);
    private bool _closed;

    internal WebKitBrowserContext(DesktopBrowsers browsers, string sessionId)
    {
        (_browsers, _sessionId) = (browsers, sessionId);
        _browsers.PageOpened += OnPageOpened;
    }

    /// <inheritdoc />
    public InstalledBrowser Browser => WebKitBrowserConnection.Installed;

    /// <inheritdoc />
    public BrowserCapabilities Capabilities { get; } = new(CpuThrottling: false, NetworkThrottling: false, Tracing: false, HeapSnapshots: false);

    /// <inheritdoc />
    public IReadOnlyList<IBrowserPage> Pages
    {
        get
        {
            var shown = _browsers.Peek(_sessionId)?.Pages ?? [];
            lock (_gate) return [.. shown.Select(page => PageOf(page.Id))];
        }
    }

    /// <inheritdoc />
    public bool IsClosed => _closed;

    /// <inheritdoc />
    public event Action<IBrowserPage>? PageOpened;

    /// <inheritdoc />
    public async Task<IBrowserPage> NewPageAsync(bool background, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        var opened = await _browsers.OpenAsync(_sessionId, null).ConfigureAwait(false);
        if (opened is not { Status: "ok", PageId: { } pageId })
        {
            throw new BrowserToolException(opened.Status == "limit" ? "limit" : "browser",
                opened.Status == "limit" ? $"The built-in browser of this session has {DesktopBrowsers.MaximumPages} tabs: close one first." : $"The built-in browser could not open a tab ({opened.Status}).");
        }

        lock (_gate) return PageOf(pageId);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IBrowserPage>> FrontPagesAsync(CancellationToken cancellationToken)
    {
        // The browser of a session is one tab of the window: the page in front there is the one the user sees.
        if (_closed || _browsers.Peek(_sessionId) is not { ActivePageId: { } active }) return Task.FromResult<IReadOnlyList<IBrowserPage>>([]);
        lock (_gate) return Task.FromResult<IReadOnlyList<IBrowserPage>>([PageOf(active)]);
    }

    /// <summary>Stops listening to the tabs of the session. The tabs stay: they are the user's too.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        _browsers.PageOpened -= OnPageOpened;
        WebKitPage[] pages;
        lock (_gate)
        {
            pages = [.. _pages.Values];
            _pages.Clear();
        }

        foreach (var page in pages) await page.DisposeAsync().ConfigureAwait(false);
    }

    private WebKitPage PageOf(string pageId)
    {
        if (!_pages.TryGetValue(pageId, out var page)) _pages[pageId] = page = new WebKitPage(_browsers, _sessionId, pageId);
        return page;
    }

    private void OnPageOpened(string sessionId, string pageId)
    {
        if (_closed || sessionId != _sessionId) return;
        WebKitPage page;
        lock (_gate) page = PageOf(pageId);
        PageOpened?.Invoke(page);
    }
}
