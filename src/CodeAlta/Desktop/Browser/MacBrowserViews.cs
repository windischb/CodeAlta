using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeoAstra;
using View = NeoAstra.NeoAstra;

namespace CodeAlta.Desktop.Browser;

/// <summary>
/// The browser views on macOS: WKWebViews on a browser environment of their own, laid above the page of the window
/// that shows the browser of their session.
/// </summary>
/// <remarks>
/// <para>
/// The environment has its own user-data root, so its own website data store: other cookies and storage, and no
/// scheme of the application. The views have no bridge to the host. A page of the web can therefore neither load the
/// application nor ask anything of it.
/// </para>
/// <para>
/// NeoAstra places a view once, when it is created, and has no method to move, hide or focus it later. A browser
/// view is therefore created on the main window and then placed, moved to the window that shows it, hidden and given
/// the keyboard through the WKWebView NeoAstra lends (<see cref="MacViewInterop"/>). It stays pinned to the top-left
/// corner of the window when the window is resized, so it moves only when the page says that its place moved.
/// </para>
/// </remarks>
internal sealed class MacBrowserViews : IBrowserViews, IAsyncDisposable
{
    /// <summary>The start of the label of a browser view: the automation of the windows leaves such views alone.</summary>
    internal const string LabelPrefix = "browser:";

    /// <summary>How often the views are asked what they show: a view reports the end of a navigation, not a new title.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>How long a page waits for a decision of the host.</summary>
    internal static readonly TimeSpan DecisionTimeout = TimeSpan.FromMinutes(10);

    // The size of a page whose session was never shown: what a session that drives it lays out and captures.
    private const int HiddenWidth = 1280, HiddenHeight = 800;

    private readonly NeoApplication _application;
    private readonly NeoWindow _window;
    private readonly string _userDataRoot;
    private readonly int[] _reservedPorts;
    private readonly UiContext _context;
    private readonly List<MacBrowserView> _live = [];
    private Task<NeoEnvironment>? _environment;
    private Timer? _poll;
    private bool _disposed;

    /// <summary>Creates the views of the windows of an application.</summary>
    /// <param name="application">The application.</param>
    /// <param name="window">The main window, on which the views are created.</param>
    /// <param name="userDataRoot">The folder of the browser data of the views, which no other browser environment uses.</param>
    /// <param name="reservedPorts">The ports of CodeAlta on this computer, which no document or frame of a view loads.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="userDataRoot"/> is blank.</exception>
    internal MacBrowserViews(NeoApplication application, NeoWindow window, string userDataRoot, IReadOnlyList<int> reservedPorts)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataRoot);
        ArgumentNullException.ThrowIfNull(reservedPorts);
        (_application, _window, _userDataRoot, _reservedPorts) = (application, window, Path.GetFullPath(userDataRoot), [.. reservedPorts]);
        _context = new UiContext(application.Dispatcher);
    }

    /// <summary>Whether a view belongs to the browser of a session, and not to the application.</summary>
    /// <param name="view">The view.</param>
    internal static bool IsBrowserView(View view) => view.ViewLabel?.StartsWith(LabelPrefix, StringComparison.Ordinal) == true;

    /// <inheritdoc />
    public bool IsSupported => OperatingSystem.IsMacOS();

    /// <inheritdoc />
    public Task<T> RunAsync<T>(Func<Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var dispatcher = _application.Dispatcher;
        if (dispatcher.CheckAccess()) return _context.Run(body);
        return Hop();

        async Task<T> Hop()
        {
            var started = await dispatcher.InvokeAsync(() => _context.Run(body)).ConfigureAwait(false);
            return await started.ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<IBrowserView> CreateAsync(string pageId, Action<Uri> openPage, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentNullException.ThrowIfNull(openPage);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("A browser view inside a window needs WKWebView.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        var environment = await (_environment ??= CreateEnvironmentAsync());
        ObjectDisposedException.ThrowIf(_disposed || _window.IsClosed, this);
        var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(_window), new NeoAstraOptions
        {
            ViewLabel = LabelPrefix + pageId,
            FillParent = false,
            Bounds = new NeoRect(0, 0, HiddenWidth, HiddenHeight),
            BridgePolicy = NeoBridgePolicy.Disabled,
            DecisionTimeout = DecisionTimeout,
        }, cancellationToken);
        try
        {
            var native = view.GetNativeHandle(NeoNativeHandleKind.WkWebView).Value;
            // NeoAstra fits a view of a window it owns to the window: this one is hidden, and keeps the place it is given.
            MacViewInterop.Prepare(native, HiddenWidth, HiddenHeight);
            var page = new MacBrowserView(this, view, native, openPage);
            _live.Add(page);
            _poll ??= new Timer(static state => ((MacBrowserViews)state!).PostPoll(), this, PollInterval, PollInterval);
            return page;
        }
        catch
        {
            await view.DisposeAsync();
            throw;
        }
    }

    /// <summary>Ends the environment of the views. The views themselves are ended by who created them.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_poll is { } poll) await poll.DisposeAsync().ConfigureAwait(false);
        var creating = _environment;
        try
        {
            await RunAsync(async () =>
            {
                foreach (var view in _live.ToArray()) await view.DisposeAsync();
                if (creating is null) return true;
                NeoEnvironment environment;
                try { environment = await creating; }
                catch (Exception exception) when (exception is not OutOfMemoryException) { return true; }
                await environment.DisposeAsync();
                return true;
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The thread of the windows is gone, and the environment with it.
        }

        _disposed = true;
    }

    private async Task<NeoEnvironment> CreateEnvironmentAsync()
        => await _application.CreateEnvironmentAsync(new NeoEnvironmentOptions { UserDataRoot = _userDataRoot });

    // The page view of a window of the application, by the label of that view.
    private View? PageOf(string label) => _application.TryGetView(label, out var view) && view is { Window.IsClosed: false } ? view : null;

    private void Remove(MacBrowserView view) => _live.Remove(view);

    private void PostPoll()
    {
        if (_disposed) return;
        try { _application.Dispatcher.Post(() => _context.Run(Poll)); }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            // The application is ending.
        }
    }

    private void Poll()
    {
        if (_disposed) return;
        foreach (var view in _live.ToArray()) view.Refresh();
    }

    /// <summary>One page: a WKWebView laid above the page of the window that shows it.</summary>
    private sealed class MacBrowserView : IBrowserView, IBrowserViewDriver
    {
        private readonly MacBrowserViews _owner;
        private readonly View _view;
        private readonly nint _native;
        private readonly Dictionary<string, MacContentWorld> _worlds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _done = new(StringComparer.Ordinal);
        private BrowserPlacement? _placement;
        private TaskCompletionSource<NeoScriptDialogDecision>? _dialogAnswer;
        private bool _disposed;

        internal MacBrowserView(MacBrowserViews owner, View view, nint native, Action<Uri> openPage)
        {
            (_owner, _view, _native) = (owner, view, native);
            State = Read();
            // A document may go where the browser may be sent; a frame of it loads what a frame loads. Neither reaches
            // the ports of CodeAlta, whose server refuses the requests of pages besides.
            view.NavigationRequested = request =>
            {
                var allowed = (!request.IsMainFrame || BrowserAddress.IsAllowed(request.Uri)) && !BrowserAddress.IsReserved(request.Uri, owner._reservedPorts);
                if (allowed && request.IsMainFrame) Navigations++;
                return ValueTask.FromResult(allowed ? NeoNavigationDecision.Allow : NeoNavigationDecision.Cancel);
            };
            // A new window is a new page of the same session.
            view.NewWindowRequested = request =>
            {
                if (request.TargetUri is { } target && BrowserAddress.IsAllowed(target) && target.AbsoluteUri != BrowserAddress.Blank) openPage(target);
                return ValueTask.FromResult(NeoNewWindowDecision.Cancel);
            };
            view.PermissionRequested = static _ => ValueTask.FromResult(NeoPermissionDecision.DenyOnce);
            view.DownloadRequested = static _ => ValueTask.FromResult(NeoDownloadDecision.Cancel);
            view.NavigationCompleted += (_, _) =>
            {
                Refresh();
                Loaded?.Invoke();
            };
            view.ScriptDialogRequested = OnDialogAsync;
        }

        public BrowserViewState State { get; private set; }

        public IBrowserViewDriver? Driver => _disposed ? null : this;

        public View View => _view;

        public nint Native => _native;

        public long Navigations { get; private set; }

        public NeoScriptDialogRequest? Dialog { get; private set; }

        public Func<NeoScriptDialogRequest, (bool Accept, string? Text)?>? DialogPolicy { get; set; }

        public event Action? Loaded;

        public bool AnswerDialog(bool accept, string? text)
        {
            if (_dialogAnswer is not { } answer) return false;
            var kind = Dialog?.Kind;
            _dialogAnswer = null;
            Dialog = null;
            answer.TrySetResult(!accept ? NeoScriptDialogDecision.Cancel
                : kind == NeoScriptDialogKind.Prompt ? NeoScriptDialogDecision.AcceptPrompt(text ?? string.Empty) : NeoScriptDialogDecision.Accept);
            Refresh();
            return true;
        }

        public bool Once(string key) => _done.Add(key);

        public MacContentWorld World(string name, string? script)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("A world of CodeAlta needs WKWebView.");
            if (!_worlds.TryGetValue(name, out var world)) _worlds[name] = world = new MacContentWorld(_native, name, script);
            return world;
        }

        // A dialog of the page: answered at once by who drives the page, or kept open until someone answers it.
        private async ValueTask<NeoScriptDialogDecision> OnDialogAsync(NeoScriptDialogRequest request)
        {
            if (DialogPolicy?.Invoke(request) is { } decided)
            {
                return !decided.Accept ? NeoScriptDialogDecision.Cancel
                    : request.Kind == NeoScriptDialogKind.Prompt ? NeoScriptDialogDecision.AcceptPrompt(decided.Text ?? request.DefaultText ?? string.Empty) : NeoScriptDialogDecision.Accept;
            }

            _dialogAnswer?.TrySetResult(NeoScriptDialogDecision.Cancel);
            var answer = new TaskCompletionSource<NeoScriptDialogDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            (_dialogAnswer, Dialog) = (answer, request);
            Refresh();
            return await answer.Task.ConfigureAwait(true);
        }

        public event Action? Changed;

        public void Navigate(Uri address)
        {
            ArgumentNullException.ThrowIfNull(address);
            if (_disposed) return;
            _ = Quietly(_view.NavigateAsync(address));
            Refresh();
        }

        public void GoBack() => Do(_view.GoBack);

        public void GoForward() => Do(_view.GoForward);

        public void Reload() => Do(_view.Reload);

        public void Stop() => Do(_view.Stop);

        public void Place(BrowserPlacement? placement)
        {
            if (_disposed || !OperatingSystem.IsMacOS()) return;
            var page = placement is null ? null : _owner.PageOf(placement.Window);
            if (placement is null || page is null)
            {
                if (_placement is not null) Hide();
                _placement = null;
                return;
            }

            var pageNative = page.GetNativeHandle(NeoNativeHandleKind.WkWebView).Value;
            MacViewInterop.Show(_native, pageNative, placement.X, placement.Y, placement.Width, placement.Height);
            _placement = placement;
        }

        public void Focus()
        {
            if (_disposed || _placement is null || !OperatingSystem.IsMacOS()) return;
            MacViewInterop.Focus(_native);
        }

        public async ValueTask<BrowserCapture?> CaptureAsync(CancellationToken cancellationToken)
        {
            if (_disposed) return null;
            try
            {
                // WKWebView captures a view that is hidden too, and as it is now.
                var image = await _view.CaptureAsync(new NeoCaptureOptions { Format = NeoCaptureFormat.Jpeg, Quality = 80 }, cancellationToken);
                return new BrowserCapture(image.Data, image.ContentType, image.Width, image.Height);
            }
            catch (Exception exception) when (exception is InvalidOperationException or NeoAstraException or ObjectDisposedException or ArgumentException)
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            if (_placement is not null) Hide();
            _disposed = true;
            AnswerDialog(false, null);
            foreach (var world in _worlds.Values) world.Dispose();
            _worlds.Clear();
            _owner.Remove(this);
            await _view.DisposeAsync();
        }

        internal void Refresh()
        {
            if (_disposed) return;
            var state = Read();
            if (state == State) return;
            State = state;
            Changed?.Invoke();
        }

        // Hides the view; a view that held the keyboard gives it to the page of its window.
        private void Hide()
        {
            if (!OperatingSystem.IsMacOS()) return;
            var page = _placement is { } shown ? _owner.PageOf(shown.Window) : null;
            MacViewInterop.Hide(_native, page?.GetNativeHandle(NeoNativeHandleKind.WkWebView).Value ?? 0);
        }

        private BrowserViewState Read()
            => new(_view.Source?.AbsoluteUri ?? BrowserAddress.Blank, _view.Title ?? string.Empty, _view.IsLoading, _view.CanGoBack, _view.CanGoForward,
                Dialog is { } dialog ? new BrowserDialog(dialog.Kind switch
                {
                    NeoScriptDialogKind.Confirm => "confirm",
                    NeoScriptDialogKind.Prompt => "prompt",
                    NeoScriptDialogKind.BeforeUnload => "beforeunload",
                    _ => "alert",
                }, BrowserDialog.Shorten(dialog.Message), dialog.DefaultText is null ? null : BrowserDialog.Shorten(dialog.DefaultText)) : null);

        private void Do(Action action)
        {
            if (_disposed) return;
            try { action(); }
            catch (Exception exception) when (exception is InvalidOperationException or NeoAstraException or ObjectDisposedException)
            {
                // The view has nothing to go to, or is going away.
            }

            Refresh();
        }

        private static async Task Quietly(ValueTask navigation)
        {
            try { await navigation; }
            catch (Exception exception) when (exception is InvalidOperationException or NeoAstraException or ObjectDisposedException or OperationCanceledException)
            {
                // An address that cannot be loaded shows the error page of the browser.
            }
        }
    }

    private sealed class UiContext(NeoDispatcher dispatcher) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            try { dispatcher.Post(() => Run(() => callback(state))); }
            catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
            {
                // The application is ending: what waited for the thread of the windows does not go on.
            }
        }

        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (dispatcher.CheckAccess()) Run(() => callback(state));
            else dispatcher.InvokeAsync(() => Run(() => callback(state))).AsTask().GetAwaiter().GetResult();
        }

        public override SynchronizationContext CreateCopy() => this;

        internal void Run(Action action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { action(); }
            finally { SetSynchronizationContext(previous); }
        }

        internal T Run<T>(Func<T> body)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { return body(); }
            finally { SetSynchronizationContext(previous); }
        }
    }
}

/// <summary>
/// What NeoAstra does not do for a view it lends: placing it in any window, hiding it and giving it the keyboard,
/// through the Objective-C runtime. Every call is made on the main thread.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacViewInterop
{
    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    private const nint NSViewMinYMargin = 8; // pinned to the top of an unflipped superview
    private const nint NSWindowAbove = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect(double x, double y, double width, double height)
    {
        public double X = x, Y = y, Width = width, Height = height;
    }

    /// <summary>Hides a view that was just created and pins it to the top-left corner of its superview.</summary>
    internal static void Prepare(nint view, int width, int height)
    {
        SendByte(view, Selector("setHidden:"), 1);
        Send(view, Selector("setAutoresizingMask:"), NSViewMinYMargin);
        SetFrame(view, new CGRect(0, 0, width, height));
    }

    /// <summary>
    /// Shows a view over a rectangle of a page view, in the CSS pixels of that page: in the window of the page,
    /// above it, moving the view there from another window first.
    /// </summary>
    internal static void Show(nint view, nint page, double x, double y, double width, double height)
    {
        var window = Send(page, Selector("window"));
        if (window == 0) return;
        var content = Send(window, Selector("contentView"));
        if (content == 0) return;
        if (Send(view, Selector("superview")) != content)
        {
            // Keeps what the page holds: the same view in another window, not a new one.
            Send(view, Selector("retain"));
            Send(view, Selector("removeFromSuperview"));
            SendSubview(content, Selector("addSubview:positioned:relativeTo:"), view, NSWindowAbove, page);
            Send(view, Selector("release"));
            Send(view, Selector("setAutoresizingMask:"), NSViewMinYMargin);
        }

        var zoom = GetDouble(page, Selector("pageZoom")) * GetDouble(page, Selector("magnification"));
        if (!(zoom > 0)) zoom = 1;
        var frame = ConvertRect(page, Selector("convertRect:toView:"), new CGRect(x * zoom, y * zoom, width * zoom, height * zoom), content);
        SetFrame(view, frame);
        if (GetByte(view, Selector("isHidden")) != 0) SendByte(view, Selector("setHidden:"), 0);
    }

    /// <summary>Hides a view; when it holds the keyboard of its window, the page view gets it, when there is one.</summary>
    internal static void Hide(nint view, nint page)
    {
        var window = Send(view, Selector("window"));
        var holds = window != 0 && Holds(Send(window, Selector("firstResponder")), view);
        SendByte(view, Selector("setHidden:"), 1);
        if (holds && page != 0 && Send(page, Selector("window")) == window) GetByte(window, Selector("makeFirstResponder:"), page);
    }

    /// <summary>Gives a view the keyboard of its window.</summary>
    internal static void Focus(nint view)
    {
        var window = Send(view, Selector("window"));
        if (window != 0) GetByte(window, Selector("makeFirstResponder:"), view);
    }

    /// <summary>Sets the user agent of a view, or gives it back its own with null.</summary>
    internal static void SetUserAgent(nint view, string? userAgent)
        => Send(view, Selector("setCustomUserAgent:"), userAgent is null ? 0 : SendText(objc_getClass("NSString"), Selector("stringWithUTF8String:"), userAgent));

    private static bool Holds(nint responder, nint view)
        => responder != 0 && GetByte(responder, Selector("isKindOfClass:"), objc_getClass("NSView")) != 0
            && (responder == view || GetByte(responder, Selector("isDescendantOf:"), view) != 0);

    private static void SetFrame(nint view, CGRect frame) => SendRect(view, Selector("setFrame:"), frame);

    private static nint Selector(string name) => sel_registerName(name);

    [DllImport(ObjectiveC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjectiveC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint argument);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern void SendByte(nint receiver, nint selector, byte value);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern byte GetByte(nint receiver, nint selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern byte GetByte(nint receiver, nint selector, nint argument);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern double GetDouble(nint receiver, nint selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern void SendRect(nint receiver, nint selector, CGRect rect);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern CGRect ConvertRect(nint receiver, nint selector, CGRect rect, nint view);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern void SendSubview(nint receiver, nint selector, nint view, nint place, nint relative);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint SendText(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
}
