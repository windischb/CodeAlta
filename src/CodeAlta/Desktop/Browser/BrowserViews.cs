namespace CodeAlta.Desktop.Browser;

/// <summary>
/// Where the browser of a session is shown: a rectangle of the page of one window of the application, in the CSS
/// pixels of that page, from its top-left corner. The host turns it into the units of the window, with the zoom of
/// the page.
/// </summary>
/// <param name="Window">The label of the view of the window whose page shows the browser.</param>
/// <param name="X">The distance from the left edge of the page.</param>
/// <param name="Y">The distance from the top edge of the page.</param>
/// <param name="Width">The width; positive.</param>
/// <param name="Height">The height; positive.</param>
internal sealed record BrowserPlacement(string Window, double X, double Y, double Width, double Height)
{
    /// <summary>The largest coordinate or size that is taken, in CSS pixels.</summary>
    internal const double Maximum = 32767;

    /// <summary>Whether the rectangle has an area and lies within what a window can have.</summary>
    internal bool IsValid => !string.IsNullOrWhiteSpace(Window) && Window.Length <= 128
        && double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height)
        && Width >= 1 && Height >= 1 && Width <= Maximum && Height <= Maximum && Math.Abs(X) <= Maximum && Math.Abs(Y) <= Maximum;
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
/// The native view of one page of the browser: a browser view of its own, which lies above the page of the window
/// that shows it. Every member is used on the thread of the windows.
/// </summary>
internal interface IBrowserView : IAsyncDisposable
{
    /// <summary>Gets what the page shows of itself now.</summary>
    BrowserViewState State { get; }

    /// <summary>Occurs on the thread of the windows when <see cref="State"/> changed.</summary>
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

    /// <summary>
    /// Shows the view at a place of the page of a window, moving it to that window when it was in another one, or
    /// hides it. A view that holds the keyboard when it is hidden gives it back to the page of its window.
    /// </summary>
    /// <param name="placement">Where the view is shown; null hides it.</param>
    void Place(BrowserPlacement? placement);

    /// <summary>Gives the view the keyboard, when it is shown.</summary>
    void Focus();

    /// <summary>Takes a picture of what the view shows, also while it is hidden.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The picture, or null when the view could not be captured.</returns>
    ValueTask<BrowserCapture?> CaptureAsync(CancellationToken cancellationToken);
}

/// <summary>Creates the native views of the browser, and runs work on the thread they belong to.</summary>
internal interface IBrowserViews
{
    /// <summary>Gets whether this system can show a browser view inside a window of the application.</summary>
    bool IsSupported { get; }

    /// <summary>Runs work on the thread of the windows; every continuation of it runs there too.</summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="body">The work.</param>
    /// <returns>What the work returned.</returns>
    /// <exception cref="ObjectDisposedException">The application is ending.</exception>
    Task<T> RunAsync<T>(Func<Task<T>> body);

    /// <summary>Creates the view of a page, hidden. It is called on the thread of the windows.</summary>
    /// <param name="pageId">The identifier of the page, which names the view.</param>
    /// <param name="openPage">
    /// What the view calls, on the thread of the windows, when its document asks for a new window: the address is
    /// opened in a page of the same session.
    /// </param>
    /// <param name="cancellationToken">Cancels the creation.</param>
    /// <returns>The view.</returns>
    ValueTask<IBrowserView> CreateAsync(string pageId, Action<Uri> openPage, CancellationToken cancellationToken);
}
