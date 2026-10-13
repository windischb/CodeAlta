namespace CodeAlta.Desktop.Browser;

/// <summary>A rectangle of the client area of the window, in the pixels of its display, from its top-left corner.</summary>
/// <param name="X">The distance from the left edge.</param>
/// <param name="Y">The distance from the top edge.</param>
/// <param name="Width">The width; positive.</param>
/// <param name="Height">The height; positive.</param>
internal readonly record struct BrowserBounds(int X, int Y, int Width, int Height)
{
    /// <summary>The largest coordinate or size that is taken, in pixels.</summary>
    internal const int Maximum = 32767;

    /// <summary>Whether the rectangle has an area and lies within what a window can have.</summary>
    internal bool IsValid => Width > 0 && Height > 0 && Width <= Maximum && Height <= Maximum && Math.Abs(X) <= Maximum && Math.Abs(Y) <= Maximum;
}

/// <summary>What a page of the browser shows of itself.</summary>
/// <param name="Address">The address of its document.</param>
/// <param name="Title">The title of its document; empty when it has none.</param>
/// <param name="Loading">Whether a document is being loaded.</param>
/// <param name="CanGoBack">Whether its history has an earlier document.</param>
/// <param name="CanGoForward">Whether its history has a later document.</param>
internal sealed record BrowserViewState(string Address, string Title, bool Loading, bool CanGoBack, bool CanGoForward);

/// <summary>A picture of what a page shows.</summary>
/// <param name="Data">The encoded image.</param>
/// <param name="MediaType">The media type of <paramref name="Data"/>, such as <c>image/jpeg</c>.</param>
/// <param name="Width">The width of the image, in pixels.</param>
/// <param name="Height">The height of the image, in pixels.</param>
internal sealed record BrowserCapture(ReadOnlyMemory<byte> Data, string MediaType, int Width, int Height);

/// <summary>
/// The native view of one page of the browser: a second browser view of the window, which lies above the page of
/// the application. Every member is used on the thread of the window.
/// </summary>
internal interface IBrowserView : IAsyncDisposable
{
    /// <summary>Gets what the page shows of itself now.</summary>
    BrowserViewState State { get; }

    /// <summary>Occurs on the thread of the window when <see cref="State"/> changed.</summary>
    event Action? Changed;

    /// <summary>Loads an address, which <see cref="BrowserAddress.IsAllowed"/> accepted.</summary>
    /// <param name="address">The address.</param>
    void Navigate(Uri address);

    /// <summary>Goes to the earlier document of the history.</summary>
    void GoBack();

    /// <summary>Goes to the later document of the history.</summary>
    void GoForward();

    /// <summary>Loads the document again.</summary>
    void Reload();

    /// <summary>Stops loading.</summary>
    void Stop();

    /// <summary>Opens the developer tools of the page, in a window of their own.</summary>
    void OpenDevTools();

    /// <summary>Shows the view at a place of the window, or hides it.</summary>
    /// <param name="bounds">Where the view is shown; null hides it.</param>
    void Place(BrowserBounds? bounds);

    /// <summary>Gives the view the keyboard.</summary>
    void Focus();

    /// <summary>Takes a picture of what the view shows. A view that is hidden is captured out of sight.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The picture, or null when the view could not be captured.</returns>
    ValueTask<BrowserCapture?> CaptureAsync(CancellationToken cancellationToken);
}

/// <summary>Creates the native views of the browser, and runs work on the thread they belong to.</summary>
internal interface IBrowserViews
{
    /// <summary>Gets whether this system can show a browser view inside the window.</summary>
    bool IsSupported { get; }

    /// <summary>Runs work on the thread of the window; every continuation of it runs there too.</summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="body">The work.</param>
    /// <returns>What the work returned.</returns>
    /// <exception cref="ObjectDisposedException">The application is ending.</exception>
    Task<T> RunAsync<T>(Func<Task<T>> body);

    /// <summary>Creates the view of a page, hidden. It is called on the thread of the window.</summary>
    /// <param name="pageId">The identifier of the page, which names the view.</param>
    /// <param name="openPage">What the view calls, on the thread of the window, when its document asks for a new window: the address is opened in a page of the same session.</param>
    /// <param name="cancellationToken">Cancels the creation.</param>
    /// <returns>The view.</returns>
    ValueTask<IBrowserView> CreateAsync(string pageId, Action<Uri> openPage, CancellationToken cancellationToken);
}
