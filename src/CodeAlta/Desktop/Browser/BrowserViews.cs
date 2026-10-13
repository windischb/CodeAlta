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
/// <param name="Dialog">The JavaScript dialog its document has open and waits in, or null.</param>
internal sealed record BrowserViewState(string Address, string Title, bool Loading, bool CanGoBack, bool CanGoForward, BrowserDialog? Dialog = null);

/// <summary>
/// A JavaScript dialog of a page: <c>alert</c>, <c>confirm</c>, <c>prompt</c>, or the question before a document is
/// left. The document runs no script until the dialog is answered.
/// </summary>
/// <param name="Kind"><c>alert</c>, <c>confirm</c>, <c>prompt</c> or <c>beforeunload</c>.</param>
/// <param name="Message">What the page says; the text of the page, shortened.</param>
/// <param name="DefaultText">The text a prompt starts with; null for the other kinds.</param>
internal sealed record BrowserDialog(string Kind, string Message, string? DefaultText)
{
    /// <summary>The longest message or default text that is kept, in UTF-16 units.</summary>
    internal const int MaximumTextLength = 4000;

    /// <summary>A dialog with its texts shortened to what is kept.</summary>
    /// <param name="kind">The kind of the dialog.</param>
    /// <param name="message">What the page says.</param>
    /// <param name="defaultText">The text a prompt starts with.</param>
    internal static BrowserDialog Create(string kind, string? message, string? defaultText)
        => new(kind, Shorten(message) ?? string.Empty, kind == "prompt" ? Shorten(defaultText) ?? string.Empty : null);

    private static string? Shorten(string? text) => text is null || text.Length <= MaximumTextLength ? text : text[..MaximumTextLength];
}

/// <summary>
/// The JavaScript dialogs of the browser views. The automation that drives the views takes the dialogs of their
/// pages: it is where a view learns of its dialog, and where the dialog is answered.
/// </summary>
internal interface IBrowserDialogs
{
    /// <summary>The dialog the page of a view has open.</summary>
    /// <param name="viewLabel">The label the view was created with.</param>
    /// <returns>The dialog, or null when the page has none or the view is not known.</returns>
    BrowserDialog? Find(string viewLabel);

    /// <summary>Answers the dialog the page of a view has open.</summary>
    /// <param name="viewLabel">The label the view was created with.</param>
    /// <param name="accept">Whether the dialog is accepted, or dismissed.</param>
    /// <param name="text">The text of a prompt that is accepted; null for its default text.</param>
    /// <returns>Whether a dialog was answered; false when somebody else answered it first.</returns>
    ValueTask<bool> AnswerAsync(string viewLabel, bool accept, string? text);
}

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

    /// <summary>
    /// Makes the view one that can be captured, until <see cref="EndCapture"/>: a view that is hidden is shown past
    /// the edge of the window, where nobody sees it. A view that is shown stays where it is.
    /// </summary>
    void BeginCapture();

    /// <summary>Ends what <see cref="BeginCapture"/> began: a view that has no place in the window is hidden again.</summary>
    void EndCapture();

    /// <summary>
    /// Runs a script in the document of the page and returns what it gives, as JSON. The picker of annotations,
    /// which the view adds to every document, is called this way: the view has no bridge.
    /// </summary>
    /// <param name="script">The script.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The result as JSON, or null when the script gave none or the page could not run it.</returns>
    ValueTask<string?> EvaluateAsync(string script, CancellationToken cancellationToken);

    /// <summary>Takes a picture of a part of what the view shows. A view that is hidden is captured out of sight.</summary>
    /// <param name="region">The part, in CSS pixels from the top-left corner of the viewport.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The picture, or null when the view could not be captured.</returns>
    ValueTask<BrowserCapture?> CaptureRegionAsync(BrowserBounds region, CancellationToken cancellationToken);

    /// <summary>Answers the JavaScript dialog the page has open (<see cref="BrowserViewState.Dialog"/>).</summary>
    /// <param name="accept">Whether the dialog is accepted, or dismissed.</param>
    /// <param name="text">The text of a prompt that is accepted; null for its default text.</param>
    /// <returns>Whether a dialog was answered; false when the page had none any more.</returns>
    ValueTask<bool> AnswerDialogAsync(bool accept, string? text);

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
