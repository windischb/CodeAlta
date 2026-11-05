using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using CodeAlta.Browser.Agent;
using CodeAlta.Browser.Drivers;
using CodeAlta.Browser.Tools;
using NeoAstra;

namespace CodeAlta.Desktop.Browser.WebKit;

/// <summary>
/// A browser tab of the browser built into the application, as the tools of an agent drive it: the same tab the user
/// sees beside the session. Coordinates are CSS pixels of the viewport.
/// </summary>
/// <remarks>
/// <para>
/// The agent of CodeAlta runs in a JavaScript world of its own (<see cref="MacContentWorld"/>), which the page cannot
/// see. Input is real: AppKit events sent to the view (<see cref="MacInput"/>). What WKWebView has no way for is said
/// so: the files of a file input, a capture of the whole document, the bodies of requests, emulation beyond the user
/// agent, traces.
/// </para>
/// <para>
/// The console and the requests of the page are recorded by a script of the page itself and read every half second:
/// they are what the page says of itself.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class WebKitPage : IBrowserPage, IAsyncDisposable
{
    /// <summary>How long a call of the agent, or a script, may take.</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How often the console and the requests the page recorded are read.</summary>
    internal static readonly TimeSpan LogInterval = TimeSpan.FromMilliseconds(500);

    private const string LogKey = "codealta.log";

    private readonly DesktopBrowsers _browsers;
    private readonly string _sessionId;
    private readonly Timer _logClock;
    private long _navigations;
    private long _logged;
    private PageDialog? _dialog;
    private bool _disposed;

    /// <summary>Creates the page of a browser tab of a session.</summary>
    /// <param name="browsers">The browsers of the sessions.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="pageId">The browser tab.</param>
    internal WebKitPage(DesktopBrowsers browsers, string sessionId, string pageId)
    {
        (_browsers, _sessionId, PageId) = (browsers, sessionId, pageId);
        _logClock = new Timer(static state => _ = ((WebKitPage)state!).ReadLogAsync(), this, LogInterval, LogInterval);
    }

    /// <summary>Gets the browser tab.</summary>
    internal string PageId { get; }

    private BrowserPageInfo? Info => _browsers.Peek(_sessionId)?.Pages.FirstOrDefault(page => page.Id == PageId);

    /// <inheritdoc />
    public string Url => Info?.Address ?? BrowserAddress.Blank;

    /// <inheritdoc />
    public string Title => Info?.Title ?? string.Empty;

    /// <inheritdoc />
    public bool IsClosed => _disposed || (_browsers.Peek(_sessionId) is { } session && session.Pages.All(page => page.Id != PageId));

    /// <inheritdoc />
    public BrowserFrame MainFrame { get; } = new("main");

    /// <inheritdoc />
    public PageLog Log { get; } = new();

    /// <inheritdoc />
    public PageDialog? Dialog => _dialog;

    /// <inheritdoc />
    public long Navigations => Interlocked.Read(ref _navigations);

    // ---- Navigation ------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task NavigateAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var before = await ReadNavigationsAsync().ConfigureAwait(false);
        var result = await _browsers.NavigateAsync(_sessionId, PageId, url).ConfigureAwait(false);
        Check(result.Status, url);
        await WaitLoadedAsync(before, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task TraverseAsync(int delta, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var page = Info;
        if (delta < 0 && page?.CanGoBack != true) throw new BrowserToolException("no-history", "The page has no earlier entry in its history.");
        if (delta > 0 && page?.CanGoForward != true) throw new BrowserToolException("no-history", "The page has no later entry in its history.");
        var before = await ReadNavigationsAsync().ConfigureAwait(false);
        Check((await _browsers.CommandAsync(_sessionId, PageId, delta < 0 ? BrowserCommand.Back : BrowserCommand.Forward).ConfigureAwait(false)).Status, null);
        await WaitLoadedAsync(before, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReloadAsync(bool ignoreCache, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var before = await ReadNavigationsAsync().ConfigureAwait(false);
        Check((await _browsers.CommandAsync(_sessionId, PageId, BrowserCommand.Reload).ConfigureAwait(false)).Status, null);
        await WaitLoadedAsync(before, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> WaitForNavigationAsync(long navigationsBefore, TimeSpan startWindow, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        while (await ReadNavigationsAsync().ConfigureAwait(false) <= navigationsBefore)
        {
            if (DateTime.UtcNow - started > startWindow) return false;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        await WaitLoadedAsync(navigationsBefore, timeout, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Waits for a document that started after `before` to finish loading.
    private async Task WaitLoadedAsync(long before, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var (navigations, loading) = await DriveAsync(driver => Task.FromResult((driver.Navigations, driver.View.IsLoading))).ConfigureAwait(false);
                Interlocked.Exchange(ref _navigations, navigations);
                if (navigations > before && !loading) return;
                await Task.Delay(50, limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BrowserToolException("timeout", $"The page did not finish loading within {timeout.TotalSeconds:0} seconds.");
        }
    }

    private async Task<long> ReadNavigationsAsync()
    {
        var navigations = await DriveAsync(driver => Task.FromResult(driver.Navigations)).ConfigureAwait(false);
        Interlocked.Exchange(ref _navigations, navigations);
        return navigations;
    }

    // ---- Scripts -------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<JsonElement> CallAgentAsync(BrowserFrame frame, string operation, string optionsJson, CancellationToken cancellationToken)
    {
        if (frame != MainFrame) throw new BrowserToolException("frame", "The built-in browser reaches the main document of a page only, not its frames.");
        using (JsonDocument.Parse(optionsJson)) { }
        var expression = $"globalThis.__codealtaBrowserAgent ? globalThis.__codealtaBrowserAgent.call({JsonSerializer.Serialize(operation)}, {optionsJson}) : {JsonSerializer.Serialize(BrowserAgent.Missing)}";
        for (var attempt = 0; ; attempt++)
        {
            var answer = await DriveAsync(driver => driver.World(BrowserAgent.WorldName, BrowserAgent.Source).CallAsync(expression, CallTimeout, cancellationToken)).ConfigureAwait(false);
            if (answer.Error is { } error) throw new BrowserToolException(error == "timeout" ? "timeout" : "script-error", error == "timeout" ? "The page did not answer in time." : error);
            var text = answer.Json is null ? null : JsonSerializer.Deserialize<string>(answer.Json);
            if (BrowserAgent.ReadAnswer(text) is { } value) return value;
            if (attempt > 2) throw new BrowserToolException("no-agent", "The page did not take CodeAlta's agent.");
            // A document that started before the world was there: its agent is added now.
            await DriveAsync(driver =>
            {
                driver.World(BrowserAgent.WorldName, BrowserAgent.Source).Run(BrowserAgent.Source);
                return Task.FromResult(true);
            }).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<BrowserFrame?> ChildFrameAsync(BrowserFrame parent, string uid, CancellationToken cancellationToken)
        => Task.FromResult<BrowserFrame?>(null);

    /// <inheritdoc />
    public async Task<string?> EvaluateAsync(string source, bool isFunction, IReadOnlyList<(BrowserFrame Frame, string Uid)> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(arguments);
        var token = Guid.NewGuid().ToString("N");
        // The elements are found by the agent in its world and marked, then taken in the page's world, which shares the document.
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Frame != MainFrame) throw new BrowserToolException("frame", "The built-in browser reaches the main document of a page only, not its frames.");
            var mark = $"(function () {{ const agent = globalThis.__codealtaBrowserAgent; if (!agent) throw new Error('No snapshot found. Use take_snapshot to capture one.'); agent.element({JsonSerializer.Serialize(arguments[index].Uid)}).setAttribute('data-codealta-argument', {JsonSerializer.Serialize(token + index.ToString(CultureInfo.InvariantCulture))}); return true; }})()";
            var marked = await DriveAsync(driver => driver.World(BrowserAgent.WorldName, BrowserAgent.Source).CallAsync(mark, CallTimeout, cancellationToken)).ConfigureAwait(false);
            if (marked.Error is { } error) throw new BrowserToolException("stale", error);
        }

        var key = JsonSerializer.Serialize("codealta.evaluate." + token);
        var call = isFunction ? $"({source})(...args)" : $"(0, eval)({JsonSerializer.Serialize(source)})";
        var start = $$"""
            (function () {
              const key = Symbol.for({{key}});
              const args = [];
              for (let index = 0; index < {{arguments.Count}}; index++) {
                const element = document.querySelector('[data-codealta-argument="{{token}}' + index + '"]');
                if (element) element.removeAttribute('data-codealta-argument');
                args.push(element);
              }
              Promise.resolve().then(() => {{call}}).then(value => {
                let json;
                try { json = value === undefined ? undefined : JSON.stringify(value); } catch (error) { json = JSON.stringify(String(value)); }
                window[key] = { done: true, json: json === undefined ? null : json, undefined: value === undefined };
              }, error => { const message = error && error.message !== undefined ? String(error.message) : String(error); window[key] = { done: true, error: error && error.stack ? message + '\n' + error.stack : message }; });
              return true;
            })()
            """;
        var read = $$"""(function () { const key = Symbol.for({{key}}); const result = window[key]; if (!result) return null; delete window[key]; return JSON.stringify(result); })()""";
        await DriveAsync(driver => Evaluate(driver, start, cancellationToken)).ConfigureAwait(false);
        var started = DateTime.UtcNow;
        while (true)
        {
            var text = await DriveAsync(driver => Evaluate(driver, read, cancellationToken)).ConfigureAwait(false);
            if (text is not null && JsonSerializer.Deserialize<string>(text) is { } json)
            {
                using var result = JsonDocument.Parse(json);
                var root = result.RootElement;
                if (root.TryGetProperty("error", out var error)) throw new BrowserToolException("script-error", error.GetString() ?? "The script failed.");
                return root.TryGetProperty("undefined", out var undefined) && undefined.ValueKind == JsonValueKind.True ? null
                    : root.TryGetProperty("json", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "null";
            }

            if (DateTime.UtcNow - started > CallTimeout) throw new BrowserToolException("timeout", "The script did not finish in time.");
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AddInitScriptAsync(string source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return await DriveAsync(async driver => (IAsyncDisposable)await driver.View.AddScriptAsync(source, new NeoScriptOptions(), cancellationToken).ConfigureAwait(true)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SetFilesAsync(BrowserFrame frame, string uid, IReadOnlyList<string> paths, CancellationToken cancellationToken)
        => throw new BrowserToolException("not-supported", "The built-in browser cannot set the files of a file input. Use Chrome, Edge or Firefox for an upload.");

    // ---- Input ---------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task MouseMoveAsync(BrowserPoint point, CancellationToken cancellationToken)
        => InputAsync(native => MacInput.Move(native, point.X, point.Y, dragging: false));

    /// <inheritdoc />
    public Task ClickAsync(BrowserPoint point, int clickCount, CancellationToken cancellationToken)
        => InputAsync(native =>
        {
            if (!MacInput.Move(native, point.X, point.Y, dragging: false)) return false;
            for (var click = 1; click <= Math.Max(clickCount, 1); click++)
            {
                if (!MacInput.Button(native, point.X, point.Y, down: true, click) || !MacInput.Button(native, point.X, point.Y, down: false, click)) return false;
            }

            return true;
        });

    /// <inheritdoc />
    public Task DragAsync(BrowserPoint from, BrowserPoint to, CancellationToken cancellationToken)
        => InputAsync(native =>
        {
            if (!MacInput.Move(native, from.X, from.Y, dragging: false) || !MacInput.Button(native, from.X, from.Y, down: true, 1)) return false;
            const int steps = 8;
            for (var step = 1; step <= steps; step++)
            {
                if (!MacInput.Move(native, from.X + (to.X - from.X) * step / steps, from.Y + (to.Y - from.Y) * step / steps, dragging: true)) return false;
            }

            return MacInput.Button(native, to.X, to.Y, down: false, 1);
        });

    /// <inheritdoc />
    public Task KeyAsync(BrowserKey key, bool down, int modifiers, CancellationToken cancellationToken)
    {
        // A modifier goes with the keys it holds, as their flags: AppKit sends no key event of its own for it.
        if (key.IsModifier) return Task.CompletedTask;
        return InputAsync(native => MacInput.Key(native, key.Code, key.Text, down, modifiers) ? true
            : throw new BrowserToolException("key", $"The built-in browser has no key {key.Key}."));
    }

    /// <inheritdoc />
    public Task InsertTextAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return InputAsync(native => MacInput.Type(native, text));
    }

    private async Task InputAsync(Func<nint, bool> send)
    {
        var sent = await DriveAsync(driver => Task.FromResult(send(driver.Native))).ConfigureAwait(false);
        if (!sent) throw new BrowserToolException("input", "The page of the built-in browser is not in a window: open the browser of the session.");
    }

    // ---- Pictures, dialogs, the rest ---------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<byte[]> ScreenshotAsync(ScreenshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.FullPage) throw new BrowserToolException("not-supported", "The built-in browser captures what the page shows, not the whole document.");
        var options = new NeoCaptureOptions
        {
            Format = request.Format == "jpeg" ? NeoCaptureFormat.Jpeg : NeoCaptureFormat.Png,
            Quality = request.Quality is { } quality ? Math.Clamp(quality, 1, 100) : null,
            Region = request.Clip is { } clip ? new NeoRect((int)Math.Floor(clip.X), (int)Math.Floor(clip.Y), (int)Math.Ceiling(clip.Width), (int)Math.Ceiling(clip.Height)) : null,
        };
        try
        {
            var image = await DriveAsync(async driver => await driver.View.CaptureAsync(options, cancellationToken).ConfigureAwait(true)).ConfigureAwait(false);
            return image.Data.ToArray();
        }
        catch (Exception exception) when (exception is NeoAstraException or InvalidOperationException or ArgumentException)
        {
            throw new BrowserToolException("screenshot", $"The page could not be captured: {exception.Message}", exception);
        }
    }

    /// <inheritdoc />
    public async Task HandleDialogAsync(bool accept, string? promptText, CancellationToken cancellationToken)
    {
        var answered = await DriveAsync(driver => Task.FromResult(driver.AnswerDialog(accept, promptText))).ConfigureAwait(false);
        _dialog = null;
        if (!answered) throw new BrowserToolException("no-dialog", "The page has no dialog open.");
    }

    /// <inheritdoc />
    public IDisposable AnswerDialogs(Func<PageDialog, (bool Accept, string? Text)?> policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _ = DriveAsync(driver =>
        {
            driver.DialogPolicy = request => policy(Of(request));
            return Task.FromResult(true);
        });
        return new Releaser(() => _ = DriveAsync(driver =>
        {
            driver.DialogPolicy = null;
            return Task.FromResult(true);
        }));
    }

    /// <inheritdoc />
    public Task<NetworkBodies> ReadBodiesAsync(NetworkEntry entry, CancellationToken cancellationToken)
        => Task.FromResult(new NetworkBodies(null, null));

    /// <inheritdoc />
    public Task<(int Width, int Height)?> ResizeAsync(int width, int height, CancellationToken cancellationToken)
        => throw new BrowserToolException("not-supported", "The built-in browser takes the size of its tab: make the tab wider or narrower instead.");

    /// <inheritdoc />
    public async Task EmulateAsync(EmulationSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Viewport is not null || settings.Geolocation is not null || settings.NetworkConditions is not null || settings.CpuThrottlingRate != 1
            || settings.ForcedColors is not null || settings.PrefersContrast is not null || settings.PrefersReducedMotion is not null || settings.ColorScheme is not null
            || settings.ExtraHeaders is { Count: > 0 })
        {
            throw new BrowserToolException("not-supported", "The built-in browser emulates the user agent only. Use Chrome or Edge for devices, networks and media features.");
        }

        await DriveAsync(driver =>
        {
            MacViewInterop.SetUserAgent(driver.Native, settings.UserAgent);
            return Task.FromResult(true);
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task BringToFrontAsync(CancellationToken cancellationToken)
        => Check((await _browsers.SelectAsync(_sessionId, PageId).ConfigureAwait(false)).Status, null);

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        await _browsers.CloseAsync(_sessionId, PageId).ConfigureAwait(false);
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Stops reading the log of the page.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _logClock.DisposeAsync().ConfigureAwait(false);
    }

    // ---- The log of the page -------------------------------------------------------------------------------------------

    // Records what the console says and which resources load, in the page's own world, where both happen.
    private const string LogScript = """
        (function () {
          const key = Symbol.for('codealta.log');
          if (window[key]) return;
          const log = { console: [], network: [] };
          Object.defineProperty(window, key, { value: log, enumerable: false });
          const text = value => { try { return typeof value === 'string' ? value : value instanceof Error ? String(value.stack || value) : JSON.stringify(value); } catch (_) { return String(value); } };
          for (const type of ['log', 'info', 'warn', 'error', 'debug']) {
            const original = console[type];
            console[type] = function (...args) {
              if (log.console.length < 1000) log.console.push({ type: type === 'warn' ? 'warning' : type, args: args.map(text).map(value => String(value).slice(0, 4000)) });
              return original.apply(this, args);
            };
          }
          window.addEventListener('error', event => { if (log.console.length < 1000) log.console.push({ type: 'error', args: [String(event.message || event.error)], url: event.filename, line: event.lineno, column: event.colno }); });
          window.addEventListener('unhandledrejection', event => { if (log.console.length < 1000) log.console.push({ type: 'error', args: ['Unhandled rejection: ' + text(event.reason)] }); });
          try {
            new PerformanceObserver(list => {
              for (const entry of list.getEntries()) {
                if (log.network.length < 1000) log.network.push({ url: entry.name, type: entry.initiatorType || entry.entryType, status: entry.responseStatus || 0, failed: entry.responseStatus === 0 && entry.transferSize === 0 && entry.decodedBodySize === 0 });
              }
            }).observe({ entryTypes: ['navigation', 'resource'] });
          } catch (_) { }
        })();
        """;

    private const string LogRead = "(function () { const log = window[Symbol.for('codealta.log')]; if (!log) return null; const taken = { console: log.console.splice(0), network: log.network.splice(0) }; return JSON.stringify(taken); })()";

    private async Task ReadLogAsync()
    {
        if (_disposed) return;
        try
        {
            var (text, navigations, dialog) = await DriveAsync(async driver =>
            {
                if (driver.Once(LogKey))
                {
                    await driver.View.AddScriptAsync(LogScript, new NeoScriptOptions { MainFrameOnly = true }).ConfigureAwait(true);
                    await Evaluate(driver, LogScript, CancellationToken.None).ConfigureAwait(true);
                }

                return (await Evaluate(driver, LogRead, CancellationToken.None).ConfigureAwait(true), driver.Navigations, driver.Dialog);
            }, quiet: true).ConfigureAwait(false);
            _dialog = dialog is null ? null : Of(dialog);
            var before = Interlocked.Exchange(ref _logged, navigations);
            for (var started = before; started < navigations; started++) Log.StartNavigation();
            Interlocked.Exchange(ref _navigations, Math.Max(Navigations, navigations));
            if (text is null || JsonSerializer.Deserialize<string>(text) is not { } json) return;
            using var document = JsonDocument.Parse(json);
            foreach (var message in document.RootElement.GetProperty("console").EnumerateArray())
            {
                var arguments = message.GetProperty("args").EnumerateArray().Select(static value => value.GetString() ?? string.Empty).ToList();
                Log.AddConsole(message.GetProperty("type").GetString() ?? "log", string.Join(' ', arguments), arguments,
                    message.TryGetProperty("url", out var url) ? url.GetString() : null,
                    message.TryGetProperty("line", out var line) && line.TryGetInt32(out var number) ? number : 0,
                    message.TryGetProperty("column", out var column) && column.TryGetInt32(out var at) ? at : 0, null);
            }

            foreach (var request in document.RootElement.GetProperty("network").EnumerateArray())
            {
                var entry = Log.AddRequest(Guid.NewGuid().ToString("N"), request.GetProperty("url").GetString() ?? string.Empty, "GET",
                    request.GetProperty("type").GetString() ?? "other", [], false, null);
                var status = request.GetProperty("status").GetInt32();
                var failed = request.GetProperty("failed").GetBoolean();
                Log.Update(entry.RequestId, value =>
                {
                    value.Status = status > 0 ? status : null;
                    value.Finished = true;
                    if (failed && status == 0) value.Failure = "failed";
                });
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A page that is going away, or a document without the log yet: the next round reads it.
        }
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------------

    private async Task<T> DriveAsync<T>(Func<IBrowserViewDriver, Task<T>> body, bool quiet = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var driver = await _browsers.DriveAsync(_sessionId, PageId).ConfigureAwait(false)
            ?? throw new BrowserToolException(quiet ? "gone" : "closed", "The tab of the built-in browser is closed.");
        try
        {
            return await _browsers.OnViewThreadAsync(() => body(driver)).ConfigureAwait(false);
        }
        catch (ObjectDisposedException exception)
        {
            throw new BrowserToolException("closed", "The built-in browser is closed.", exception);
        }
    }

    private static async Task<string?> Evaluate(IBrowserViewDriver driver, string script, CancellationToken cancellationToken)
    {
        try
        {
            return await driver.View.EvaluateScriptAsync(script, cancellationToken).ConfigureAwait(true);
        }
        catch (NeoAstraException exception)
        {
            throw new BrowserToolException("script-error", exception.Message, exception);
        }
    }

    private static PageDialog Of(NeoScriptDialogRequest request)
        => new(request.Kind switch { NeoScriptDialogKind.Confirm => "confirm", NeoScriptDialogKind.Prompt => "prompt", NeoScriptDialogKind.BeforeUnload => "beforeunload", _ => "alert" },
            request.Message, request.DefaultText);

    private static void Check(string status, string? url)
    {
        switch (status)
        {
            case "ok": return;
            case "invalid": throw new BrowserToolException("invalid-url", url is null ? "The request was refused." : $"The built-in browser opens http and https addresses only, not {url}.");
            case "not_found": throw new BrowserToolException("closed", "The tab of the built-in browser is closed.");
            default: throw new BrowserToolException("browser", $"The built-in browser could not do it ({status}).");
        }
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) release();
        }
    }
}
