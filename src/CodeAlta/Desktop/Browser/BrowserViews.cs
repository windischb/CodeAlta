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

/// <summary>A JavaScript dialog a page has open: it waits until someone answers it.</summary>
/// <param name="Kind"><c>alert</c>, <c>confirm</c>, <c>prompt</c>, or <c>beforeunload</c> for the question before a document is left.</param>
/// <param name="Message">The text of the page; at most <see cref="MaximumTextLength"/> UTF-16 units of it.</param>
/// <param name="DefaultText">The text a prompt proposes, or null.</param>
internal sealed record BrowserDialog(string Kind, string Message, string? DefaultText)
{
    /// <summary>The longest text that is shown of a dialog, or taken as the answer to a prompt, in UTF-16 units.</summary>
    internal const int MaximumTextLength = 4000;

    /// <summary>Text of a page, shortened to what is shown.</summary>
    /// <param name="text">The text.</param>
    internal static string Shorten(string? text) => text is null ? string.Empty : text.Length <= MaximumTextLength ? text : text[..MaximumTextLength];
}

/// <summary>What a page of the browser shows of itself.</summary>
/// <param name="Address">The address of its document.</param>
/// <param name="Title">The title of its document; empty when it has none.</param>
/// <param name="Loading">Whether a document is being loaded.</param>
/// <param name="CanGoBack">Whether its history has an earlier document.</param>
/// <param name="CanGoForward">Whether its history has a later document.</param>
/// <param name="Dialog">The JavaScript dialog the page has open, or null.</param>
internal sealed record BrowserViewState(string Address, string Title, bool Loading, bool CanGoBack, bool CanGoForward, BrowserDialog? Dialog = null);

/// <summary>A picture of what a page shows.</summary>
/// <param name="Data">The encoded image.</param>
/// <param name="MediaType">The media type of <paramref name="Data"/>, such as <c>image/jpeg</c>.</param>
/// <param name="Width">The width of the image, in pixels.</param>
/// <param name="Height">The height of the image, in pixels.</param>
internal sealed record BrowserCapture(ReadOnlyMemory<byte> Data, string MediaType, int Width, int Height);

/// <summary>
/// What drives the page of a browser view for the tools of the agent: the NeoAstra view and its native view, the
/// worlds of CodeAlta in its documents, and what it reports of its navigations. Used on the thread of the windows.
/// </summary>
internal interface IBrowserViewDriver
{
    /// <summary>Gets the NeoAstra view.</summary>
    global::NeoAstra.NeoAstra View { get; }

    /// <summary>Gets the native view: the WKWebView on macOS.</summary>
    nint Native { get; }

    /// <summary>Gets the number of documents the main frame started to load.</summary>
    long Navigations { get; }

    /// <summary>Gets the JavaScript dialog the page has open, or null.</summary>
    global::NeoAstra.NeoScriptDialogRequest? Dialog { get; }

    /// <summary>
    /// Gets or sets who answers the JavaScript dialogs of the page: given the dialog, the answer, or null to leave it
    /// open until it is answered with <see cref="AnswerDialog"/>.
    /// </summary>
    Func<global::NeoAstra.NeoScriptDialogRequest, (bool Accept, string? Text)?>? DialogPolicy { get; set; }

    /// <summary>Occurs on the thread of the windows when a document of the main frame finished loading, or failed to.</summary>
    event Action? Loaded;

    /// <summary>Answers the dialog the page has open.</summary>
    /// <returns>Whether there was one.</returns>
    bool AnswerDialog(bool accept, string? text);

    /// <summary>Whether something is done to this view for the first time: true once for each key, then false.</summary>
    /// <param name="key">What is done.</param>
    bool Once(string key);

    /// <summary>A world of CodeAlta in the documents of the page, opened with its script the first time it is asked for.</summary>
    /// <param name="name">The name of the world.</param>
    /// <param name="script">The script that runs at the start of each document in that world.</param>
    MacContentWorld World(string name, string? script);
}

/// <summary>
/// The native view of one page of the browser: a browser view of its own, which lies above the page of the window
/// that shows it. Every member is used on the thread of the windows.
/// </summary>
internal interface IBrowserView : IAsyncDisposable
{
    /// <summary>Gets what the page shows of itself now.</summary>
    BrowserViewState State { get; }

    /// <summary>Gets what drives the page for the tools of the agent, or null where the view has none.</summary>
    IBrowserViewDriver? Driver { get; }

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

    /// <summary>Answers the JavaScript dialog the page has open: <see cref="BrowserViewState.Dialog"/>.</summary>
    /// <param name="accept">Whether the dialog is accepted, or dismissed.</param>
    /// <param name="text">The text of a prompt that is accepted.</param>
    /// <returns>Whether there was a dialog.</returns>
    bool AnswerDialog(bool accept, string? text);

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
