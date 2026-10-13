using System.Runtime.InteropServices;
using NeoAstra;
using View = NeoAstra.NeoAstra;

namespace CodeAlta.Desktop.Browser;

/// <summary>
/// The browser views of the window on Windows: WebView2 views that are children of the window, above the view of
/// the application, on a browser environment of their own.
/// </summary>
/// <remarks>
/// <para>
/// The environment has its own user-data folder: another browser process, other cookies and storage, and no scheme
/// of the application. The views have no bridge to the host. A page of the web can therefore neither load the
/// application nor ask anything of it.
/// </para>
/// <para>
/// NeoAstra places a view once, when it is created, and fits a view of a window it owns to that window. A browser
/// view is therefore created on the handle of the window, which NeoAstra then leaves where it is put, and is moved,
/// hidden and given the keyboard through the WebView2 controller NeoAstra lends (<see cref="BrowserViewInterop"/>).
/// WebView2 puts a view it creates under the ones that exist: its window is raised once.
/// </para>
/// </remarks>
internal sealed class WebViewBrowserViews : IBrowserViews, IAsyncDisposable
{
    /// <summary>The start of the label of a browser view: the automation of the window leaves such views alone.</summary>
    internal const string LabelPrefix = "browser:";

    /// <summary>How often the views are asked what they show: a view reports the end of a navigation, not a new title.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

    // The size of a page whose session was never shown: what a session that drives it lays out and captures.
    private const int HiddenWidth = 1280, HiddenHeight = 800;

    // Out of sight: a view is created shown, and is hidden as soon as it exists.
    private static readonly NeoRect Offstage = new(-BrowserBounds.Maximum, -BrowserBounds.Maximum, HiddenWidth, HiddenHeight);

    private readonly NeoApplication _application;
    private readonly NeoWindow _window;
    private readonly string _userDataRoot;
    private readonly UiContext _context;
    private readonly List<WebViewBrowserView> _live = [];
    private Task<NeoEnvironment>? _environment;
    private Timer? _poll;
    private WebViewBrowserView? _keyboard;
    private bool _disposed;

    /// <summary>
    /// Gets or sets what gives the keyboard back to the page of the application, when the browser view that had it
    /// is hidden. It is called on the thread of the window.
    /// </summary>
    internal Action? KeyboardReturned { get; set; }

    /// <summary>
    /// Gives the keyboard to the browser view that had it last, when that view is still shown: for a window that is
    /// used again after another application was. It is called on the thread of the window.
    /// </summary>
    /// <returns>Whether a browser view took the keyboard; false leaves it to the page of the application.</returns>
    internal bool RestoreKeyboard()
    {
        if (_disposed || !OperatingSystem.IsWindows() || _keyboard is not { IsShown: true } view) return false;
        view.Focus();
        return true;
    }

    /// <summary>Creates the views of a window.</summary>
    /// <param name="application">The application of the window.</param>
    /// <param name="window">The window the views are shown in.</param>
    /// <param name="userDataRoot">The folder of the browser data of the views, which no other browser environment uses.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="userDataRoot"/> is blank.</exception>
    internal WebViewBrowserViews(NeoApplication application, NeoWindow window, string userDataRoot)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataRoot);
        (_application, _window, _userDataRoot) = (application, window, Path.GetFullPath(userDataRoot));
        _context = new UiContext(application.Dispatcher);
    }

    /// <summary>Whether a view belongs to the browser of a session, and not to the application.</summary>
    /// <param name="view">The view.</param>
    internal static bool IsBrowserView(View view) => view.ViewLabel?.StartsWith(LabelPrefix, StringComparison.Ordinal) == true;

    /// <inheritdoc />
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public Task<T> RunAsync<T>(Func<Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
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
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("A browser view inside the window needs WebView2.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        var environment = await (_environment ??= CreateEnvironmentAsync());
        ObjectDisposedException.ThrowIf(_disposed || _window.IsClosed, this);
        var parent = _window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
        var before = BrowserViewInterop.Children(parent);
        var view = await environment.CreateWebViewAsync(NeoAstraHost.FromWin32Hwnd(parent), new NeoAstraOptions
        {
            ViewLabel = LabelPrefix + pageId,
            FillParent = false,
            Bounds = Offstage,
            BridgePolicy = NeoBridgePolicy.Disabled,
        }, cancellationToken);
        try
        {
            var controller = view.GetNativeHandle(NeoNativeHandleKind.WebView2Controller).Value;
            BrowserViewInterop.SetVisible(controller, false);
            nint window = 0;
            foreach (var child in BrowserViewInterop.Children(parent))
            {
                if (before.Contains(child)) continue;
                BrowserViewInterop.Raise(child);
                window = child;
            }

            var page = new WebViewBrowserView(this, view, controller, window, openPage);
            _live.Add(page);
            _poll ??= new Timer(static state => ((WebViewBrowserViews)state!).PostPoll(), this, PollInterval, PollInterval);
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
        _disposed = true;
        if (_poll is { } poll) await poll.DisposeAsync().ConfigureAwait(false);
        if (_environment is not { } creating) return;
        try
        {
            await RunAsync(async () =>
            {
                NeoEnvironment environment;
                try { environment = await creating; }
                catch (Exception exception) when (exception is not OutOfMemoryException) { return true; }
                foreach (var view in _live.ToArray()) await view.DisposeAsync();
                await environment.DisposeAsync();
                return true;
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The thread of the window is gone, and the environment with it.
        }
    }

    private async Task<NeoEnvironment> CreateEnvironmentAsync()
        => await _application.CreateEnvironmentAsync(new NeoEnvironmentOptions { UserDataRoot = _userDataRoot });

    private NeoSize ClientSize => _window.IsClosed ? default : _window.ClientSize;

    private void Remove(WebViewBrowserView view)
    {
        _live.Remove(view);
        if (ReferenceEquals(_keyboard, view)) _keyboard = null;
    }

    // Which browser view holds the keyboard, as far as a window of this thread holds it: none when the page of the
    // application does. While another application is used the answer stays what it was.
    private void TrackKeyboard()
    {
        if (!OperatingSystem.IsWindows() || _window.IsClosed) return;
        var holder = BrowserViewInterop.FocusedChild(_window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value);
        if (holder != 0) _keyboard = _live.FirstOrDefault(view => view.Window == holder);
    }

    // A browser view that is hidden while it holds the keyboard gives it back to the page of the application.
    private void Hidden(WebViewBrowserView view)
    {
        TrackKeyboard();
        if (!ReferenceEquals(_keyboard, view)) return;
        _keyboard = null;
        KeyboardReturned?.Invoke();
    }

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
        TrackKeyboard();
        foreach (var view in _live.ToArray()) view.Refresh();
    }

    /// <summary>One page: a WebView2 view of the window.</summary>
    private sealed class WebViewBrowserView : IBrowserView
    {
        private readonly WebViewBrowserViews _owner;
        private readonly View _view;
        private readonly nint _controller;
        private BrowserBounds? _bounds;
        // The size the view last had in the window: the one it is captured in while it is hidden.
        private (int Width, int Height) _size = (HiddenWidth, HiddenHeight);
        private int _captures;
        private bool _disposed;

        internal WebViewBrowserView(WebViewBrowserViews owner, View view, nint controller, nint window, Action<Uri> openPage)
        {
            (_owner, _view, _controller, Window) = (owner, view, controller, window);
            State = Read();
            // A document may go where the browser may be sent; a frame of it loads what a frame loads.
            view.NavigationRequested = static request => ValueTask.FromResult(
                !request.IsMainFrame || BrowserAddress.IsAllowed(request.Uri) ? NeoNavigationDecision.Allow : NeoNavigationDecision.Cancel);
            // A new window is a new page of the same session.
            view.NewWindowRequested = request =>
            {
                if (request.TargetUri is { } target && BrowserAddress.IsAllowed(target) && target.AbsoluteUri != BrowserAddress.Blank) openPage(target);
                return ValueTask.FromResult(NeoNewWindowDecision.Cancel);
            };
            view.PermissionRequested = static _ => ValueTask.FromResult(NeoPermissionDecision.DenyOnce);
            view.DownloadRequested = static _ => ValueTask.FromResult(NeoDownloadDecision.Cancel);
            view.NavigationCompleted += (_, _) => Refresh();
        }

        /// <summary>The child window of the view, 0 when it is not known.</summary>
        internal nint Window { get; }

        /// <summary>Whether the view has a place in the window.</summary>
        internal bool IsShown => !_disposed && _bounds is not null;

        public BrowserViewState State { get; private set; }

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

        public void OpenDevTools() => Do(_view.OpenDevTools);

        public void Place(BrowserBounds? bounds)
        {
            if (_disposed || !OperatingSystem.IsWindows()) return;
            if (bounds is { } shown)
            {
                _size = (shown.Width, shown.Height);
                BrowserViewInterop.SetBounds(_controller, shown);
                if (_bounds is null) BrowserViewInterop.SetVisible(_controller, true);
            }
            else if (_bounds is not null)
            {
                _owner.Hidden(this);
                BrowserViewInterop.SetVisible(_controller, false);
            }

            _bounds = bounds;
        }

        public void Focus()
        {
            if (!_disposed && _bounds is not null && OperatingSystem.IsWindows()) BrowserViewInterop.Focus(_controller);
        }

        public void BeginCapture()
        {
            if (_disposed || !OperatingSystem.IsWindows()) return;
            // WebView2 captures a view that is shown only: a hidden one is shown past the edge of the window meanwhile,
            // in the size it last had there.
            if (_captures++ != 0 || _bounds is not null) return;
            var client = _owner.ClientSize;
            BrowserViewInterop.SetBounds(_controller, new BrowserBounds(Math.Max(client.Width, 0) + 64, 0, _size.Width, _size.Height));
            BrowserViewInterop.SetVisible(_controller, true);
        }

        public void EndCapture()
        {
            if (_captures == 0 || --_captures != 0) return;
            // The view may have been given a place while it was captured.
            if (!_disposed && _bounds is null && OperatingSystem.IsWindows()) BrowserViewInterop.SetVisible(_controller, false);
        }

        public async ValueTask<BrowserCapture?> CaptureAsync(CancellationToken cancellationToken)
        {
            if (_disposed || !OperatingSystem.IsWindows()) return null;
            BeginCapture();
            try
            {
                var image = await _view.CaptureAsync(new NeoCaptureOptions { Format = NeoCaptureFormat.Jpeg, Quality = 80 }, cancellationToken);
                return new BrowserCapture(image.Data, image.ContentType, image.Width, image.Height);
            }
            catch (Exception exception) when (exception is InvalidOperationException or NeoAstraException or ObjectDisposedException)
            {
                return null;
            }
            finally
            {
                EndCapture();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Remove(this);
            await _view.DisposeAsync();
        }

        /// <summary>Reads what the view shows, and tells when it is not what it showed.</summary>
        internal void Refresh()
        {
            if (_disposed) return;
            var state = Read();
            if (state == State) return;
            State = state;
            Changed?.Invoke();
        }

        private BrowserViewState Read()
            => new(_view.Source?.AbsoluteUri ?? BrowserAddress.Blank, _view.Title ?? string.Empty, _view.IsLoading, _view.CanGoBack, _view.CanGoForward);

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

    /// <summary>
    /// Brings every continuation of work on the views back to the thread of the window, whatever context that
    /// thread has by itself.
    /// </summary>
    private sealed class UiContext(NeoDispatcher dispatcher) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            try { dispatcher.Post(() => Run(() => callback(state))); }
            catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
            {
                // The application is ending: what waited for the thread of the window does not go on.
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
/// What NeoAstra does not offer for a view of a window: a new place, hiding, the keyboard and the order among the
/// views. On Windows these are methods of the WebView2 controller NeoAstra lends, called through its COM table as
/// <see cref="DesktopWindowFocus"/> does, and the order of the child windows of the window.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class BrowserViewInterop
{
    // ICoreWebView2Controller, after the three methods of IUnknown: get_IsVisible, put_IsVisible, get_Bounds, put_Bounds, ..., MoveFocus.
    private const int PutIsVisibleSlot = 4, PutBoundsSlot = 6, MoveFocusSlot = 12;
    private const int Programmatic = 0; // COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC: the page keeps what had the focus.

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>Shows or hides a view.</summary>
    internal static bool SetVisible(nint controller, bool visible)
        => controller != 0 && Method<IntMethod>(controller, PutIsVisibleSlot)(controller, visible ? 1 : 0) >= 0;

    /// <summary>Puts a view at a place of the client area of its window.</summary>
    internal static bool SetBounds(nint controller, BrowserBounds bounds)
        => controller != 0 && Method<RectMethod>(controller, PutBoundsSlot)(controller,
            new Rect { Left = bounds.X, Top = bounds.Y, Right = bounds.X + bounds.Width, Bottom = bounds.Y + bounds.Height }) >= 0;

    /// <summary>Gives a view the keyboard.</summary>
    internal static bool Focus(nint controller)
        => controller != 0 && Method<IntMethod>(controller, MoveFocusSlot)(controller, Programmatic) >= 0;

    /// <summary>
    /// The child window of a window that holds the keyboard, itself or through a window inside it; 0 when the
    /// window itself holds it, or no window of this thread does.
    /// </summary>
    internal static nint FocusedChild(nint window)
    {
        var holder = GetFocus();
        // A browser view is a few windows deep: the walk ends at the window, or far before this many steps.
        for (var depth = 0; holder != 0 && depth < 32; depth++)
        {
            var parent = GetParent(holder);
            if (parent == window) return holder;
            holder = parent;
        }

        return 0;
    }

    /// <summary>The child windows of a window.</summary>
    internal static HashSet<nint> Children(nint window)
    {
        var children = new HashSet<nint>();
        for (var child = GetWindow(window, 5 /* GW_CHILD: the top one */); child != 0 && children.Add(child); child = GetWindow(child, 2 /* GW_HWNDNEXT: the one below */)) { }
        return children;
    }

    /// <summary>Puts a child window above its siblings, without giving it the keyboard.</summary>
    internal static bool Raise(nint child)
        => child != 0 && SetWindowPos(child, 0 /* HWND_TOP */, 0, 0, 0, 0, 0x0013 /* SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE */);

    // The controller is a COM object borrowed from NeoAstra: its methods are called through its table, without a wrapper.
    private static T Method<T>(nint instance, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IntMethod(nint instance, int value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RectMethod(nint instance, Rect value);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetFocus();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetParent(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}