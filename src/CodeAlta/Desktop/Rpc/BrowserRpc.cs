using System.Runtime.CompilerServices;
using CodeAlta.Desktop.Browser;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page's side of the browsers of the sessions: it opens, closes and orders the pages of a session, sends them
/// to an address, and says where the pane of the session shows the page in front. The pages are native views that
/// the host owns: the page of the application draws the frame around them and never their content.
/// </summary>
[NeoRpcService("browser", Version = 1)]
internal sealed class BrowserService
{
    /// <summary>The largest picture of a page that is given to the page of the application, in bytes.</summary>
    internal const int MaximumCaptureBytes = 8 * 1024 * 1024;

    /// <summary>The most pictures of annotations that go with one prompt.</summary>
    internal const int MaximumPromptPictures = 8;

    private readonly DesktopBrowsers? _browsers;
    private readonly string? _epoch;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal BrowserService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="browsers">The browsers of the sessions.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="browsers"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal BrowserService(DesktopBrowsers browsers, string epoch)
    {
        ArgumentNullException.ThrowIfNull(browsers);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_browsers, _epoch) = (browsers, epoch);
    }

    /// <summary>The browser of a session, with the pages kept for it from an earlier run.</summary>
    [NeoRpcMethod("state")]
    public async Task<BrowserReply> StateAsync(BrowserSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.GetAsync(request.SessionId ?? string.Empty).ConfigureAwait(false));
    }

    /// <summary>Opens a page in the browser of a session and puts it in front.</summary>
    [NeoRpcMethod("open")]
    public async Task<BrowserReply> OpenAsync(BrowserOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.OpenAsync(request.SessionId ?? string.Empty, request.Address).ConfigureAwait(false));
    }

    /// <summary>Closes a page.</summary>
    [NeoRpcMethod("close")]
    public async Task<BrowserReply> CloseAsync(BrowserPageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.CloseAsync(request.SessionId ?? string.Empty, request.PageId ?? string.Empty).ConfigureAwait(false));
    }

    /// <summary>Puts a page in front.</summary>
    [NeoRpcMethod("select")]
    public async Task<BrowserReply> SelectAsync(BrowserPageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.SelectAsync(request.SessionId ?? string.Empty, request.PageId ?? string.Empty).ConfigureAwait(false));
    }

    /// <summary>Moves a page to another place of the strip.</summary>
    [NeoRpcMethod("move")]
    public async Task<BrowserReply> MoveAsync(BrowserMoveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.MoveAsync(request.SessionId ?? string.Empty, request.PageId ?? string.Empty, request.Index).ConfigureAwait(false));
    }

    /// <summary>Loads an address in a page: what was typed, read as <see cref="BrowserAddress.Parse"/> reads it.</summary>
    [NeoRpcMethod("navigate")]
    public async Task<BrowserReply> NavigateAsync(BrowserNavigateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.NavigateAsync(request.SessionId ?? string.Empty, request.PageId ?? string.Empty, request.Address).ConfigureAwait(false));
    }

    /// <summary>
    /// Asks a page to go back or forward, to load again, to stop, to open its developer tools or to take the
    /// keyboard.
    /// </summary>
    [NeoRpcMethod("command")]
    public async Task<BrowserReply> CommandAsync(BrowserCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        BrowserCommand? command = request.Command switch
        {
            "back" => BrowserCommand.Back,
            "forward" => BrowserCommand.Forward,
            "reload" => BrowserCommand.Reload,
            "stop" => BrowserCommand.Stop,
            "devtools" => BrowserCommand.DevTools,
            "focus" => BrowserCommand.Focus,
            _ => null,
        };
        if (command is null) return new("invalid");
        return Reply(await _browsers!.CommandAsync(request.SessionId ?? string.Empty, request.PageId ?? string.Empty, command.Value).ConfigureAwait(false));
    }

    /// <summary>
    /// Answers the JavaScript dialog a page has open, which the pane of the session shows in the place of the page:
    /// accepted or dismissed, with the text of a prompt.
    /// </summary>
    [NeoRpcMethod("answerDialog")]
    public async Task<BrowserReply> AnswerDialogAsync(BrowserDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.AnswerDialogAsync(request.SessionId ?? string.Empty, request.PageId ?? string.Empty, request.Accept, request.Text).ConfigureAwait(false));
    }

    /// <summary>
    /// Says where the pane of a session shows its browser, in the pixels of the display from the top-left corner of
    /// the page of the application, or that it shows none.
    /// </summary>
    [NeoRpcMethod("place")]
    public async Task<BrowserReply> PlaceAsync(BrowserPlaceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        BrowserBounds? bounds = request.Visible ? new BrowserBounds(request.X, request.Y, request.Width, request.Height) : null;
        return Reply(await _browsers!.PlaceAsync(request.SessionId ?? string.Empty, bounds).ConfigureAwait(false));
    }

    /// <summary>Lets the views of a session go, for a session the window no longer shows: its pages stay, and load again when shown.</summary>
    [NeoRpcMethod("release")]
    public async Task<BrowserReply> ReleaseAsync(BrowserSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.ReleaseAsync(request.SessionId ?? string.Empty).ConfigureAwait(false));
    }

    /// <summary>
    /// A picture of what a page shows, for the page of the application to draw in its place while the view is
    /// hidden behind something of the application.
    /// </summary>
    [NeoRpcMethod("capture")]
    public async Task<BrowserCaptureReply> CaptureAsync(BrowserPageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, 0, 0);
        var capture = await _browsers!.CaptureAsync(request.SessionId ?? string.Empty, request.PageId ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (capture is null || capture.Data.Length > MaximumCaptureBytes) return new("unavailable_page", null, 0, 0);
        return new("ok", $"data:{capture.MediaType};base64,{Convert.ToBase64String(capture.Data.Span)}", capture.Width, capture.Height);
    }

    /// <summary>The annotations of a session: the elements of its pages the user pointed at, with what should change.</summary>
    [NeoRpcMethod("annotations")]
    public async Task<BrowserAnnotationsReply> AnnotationsAsync(BrowserSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.AnnotationsAsync(request.SessionId ?? string.Empty).ConfigureAwait(false));
    }

    /// <summary>
    /// Turns pick mode on or off in a page: while it is on, a click in the page picks an element instead of
    /// reaching the page. What was picked is told with the annotations of the session.
    /// </summary>
    [NeoRpcMethod("pick")]
    public async Task<BrowserAnnotationsReply> PickAsync(BrowserPickRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.PickAsync(request.SessionId ?? string.Empty, request.PageId ?? string.Empty, request.On).ConfigureAwait(false));
    }

    /// <summary>
    /// Makes an annotation of the element that was picked, with what the user wrote. The request carries the text
    /// alone: the element is the one the host holds since the pick.
    /// </summary>
    [NeoRpcMethod("saveAnnotation")]
    public async Task<BrowserAnnotationsReply> SaveAnnotationAsync(BrowserAnnotationTextRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.SaveAnnotationAsync(request.SessionId ?? string.Empty, request.Text).ConfigureAwait(false));
    }

    /// <summary>Forgets the element that was picked and waits for its text.</summary>
    [NeoRpcMethod("discardPick")]
    public async Task<BrowserAnnotationsReply> DiscardPickAsync(BrowserSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.DiscardPickAsync(request.SessionId ?? string.Empty).ConfigureAwait(false));
    }

    /// <summary>Resolves, reopens or deletes an annotation, or goes to its element in the browser of the session.</summary>
    [NeoRpcMethod("changeAnnotation")]
    public async Task<BrowserAnnotationsReply> ChangeAnnotationAsync(BrowserAnnotationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        var session = request.SessionId ?? string.Empty;
        return Reply(request.Action switch
        {
            "resolve" => await _browsers!.ResolveAnnotationAsync(session, request.Number, "user", null).ConfigureAwait(false),
            "reopen" => await _browsers!.ReopenAnnotationAsync(session, request.Number).ConfigureAwait(false),
            "delete" => await _browsers!.DeleteAnnotationAsync(session, request.Number).ConfigureAwait(false),
            "reveal" => await _browsers!.RevealAnnotationAsync(session, request.Number).ConfigureAwait(false),
            _ => new BrowserAnnotationResult("invalid"),
        });
    }

    /// <summary>The picture of the element of an annotation, or with the number 0 of the element that was picked.</summary>
    [NeoRpcMethod("annotationImage")]
    public async Task<BrowserCaptureReply> AnnotationImageAsync(BrowserAnnotationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, 0, 0);
        var capture = await _browsers!.AnnotationImageAsync(request.SessionId ?? string.Empty, request.Number).ConfigureAwait(false);
        if (capture is null || capture.Data.Length > MaximumCaptureBytes) return new("unavailable_page", null, 0, 0);
        return new("ok", $"data:{capture.MediaType};base64,{Convert.ToBase64String(capture.Data.Span)}", capture.Width, capture.Height);
    }

    /// <summary>
    /// The open annotations of a session as a prompt for its composer: the texts of the user and the element
    /// records, labelled as what the pages reported, and the numbers of the annotations whose picture goes with it.
    /// </summary>
    [NeoRpcMethod("annotationPrompt")]
    public async Task<BrowserAnnotationPromptReply> AnnotationPromptAsync(BrowserSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, []);
        var result = await _browsers!.AnnotationsAsync(request.SessionId ?? string.Empty).ConfigureAwait(false);
        if (result.Annotations is not { } annotations) return new(result.Status, null, []);
        var open = annotations.Annotations.Select(static state => state.Annotation).Where(static annotation => annotation.IsOpen).ToArray();
        if (open.Length == 0) return new("not_found", null, []);
        var pictures = open.Where(static annotation => annotation.Image is not null).Select(static annotation => annotation.Number).Take(MaximumPromptPictures).ToArray();
        return new("ok", BrowserAnnotationText.Prompt(open, pictures.Length), pictures);
    }

    /// <summary>
    /// The browsers as they change: first whether this system has them, then each session whose browser was asked
    /// for, and a session again each time its browser or its annotations change.
    /// </summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<BrowserEvent> Watch(BrowserWatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.BrowserEvent);
    }

    internal async IAsyncEnumerable<BrowserEvent> WatchAsync(BrowserWatchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request.ExpectedEpoch) is not null) yield break;
        yield return new BrowserEvent("ready", _browsers!.IsSupported, null);
        await foreach (var change in _browsers.WatchChangesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (change.Session is { } session) yield return new BrowserEvent("session", true, Item(session));
            if (change.Annotations is { } annotations) yield return new BrowserEvent("annotations", true, null, Item(annotations));
        }
    }

    private static BrowserAnnotationsReply Reply(BrowserAnnotationResult result) => new(result.Status, result.Annotations is null ? null : Item(result.Annotations));

    private static BrowserAnnotationsItem Item(BrowserAnnotationsInfo annotations)
        => new(annotations.SessionId,
            [.. annotations.Annotations.Select(static state => new BrowserAnnotationItem(state.Annotation.Number, state.Annotation.Text, state.Annotation.Status, state.Annotation.ResolvedBy,
                state.Annotation.Note, state.Annotation.Address, state.Annotation.Title, Item(state.Annotation.Element), state.Annotation.Image is not null, state.Found))],
            annotations.PickingPageId,
            annotations.Pending is { } pending ? new BrowserPendingItem(pending.PageId, pending.Address, pending.Title, Item(pending.Element), pending.Image is not null) : null,
            annotations.Open);

    private static BrowserElementItem Item(BrowserElementRecord element) => new(element.Selector, element.Tag, element.Role, element.Name, element.Text, element.Html);

    private string? Refuse(string? expectedEpoch)
        => _browsers is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    private static BrowserReply Reply(BrowserResult result) => new(result.Status, result.Session is null ? null : Item(result.Session), result.PageId);

    private static BrowserSessionItem Item(BrowserSessionInfo session)
        => new(session.SessionId,
            [.. session.Pages.Select(static page => new BrowserPageItem(page.Id, page.Address, page.Title, page.Loading, page.CanGoBack, page.CanGoForward, page.Live,
                page.Dialog is { } dialog ? new BrowserDialogItem(dialog.Kind, dialog.Message, dialog.DefaultText) : null))],
            session.ActivePageId);
}

/// <summary>A request about the browser of a session.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
internal sealed record BrowserSessionRequest(string? ExpectedEpoch, string? SessionId);

/// <summary>A page to open.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
/// <param name="Address">What was typed as its address; null or empty for the empty page.</param>
internal sealed record BrowserOpenRequest(string? ExpectedEpoch, string? SessionId, string? Address);

/// <summary>A request about one page.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
/// <param name="PageId">The page.</param>
internal sealed record BrowserPageRequest(string? ExpectedEpoch, string? SessionId, string? PageId);

/// <summary>A page to move in the strip.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
/// <param name="PageId">The page.</param>
/// <param name="Index">Its new place, from 0.</param>
internal sealed record BrowserMoveRequest(string? ExpectedEpoch, string? SessionId, string? PageId, int Index);

/// <summary>An address for a page.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
/// <param name="PageId">The page.</param>
/// <param name="Address">What was typed as the address.</param>
internal sealed record BrowserNavigateRequest(string? ExpectedEpoch, string? SessionId, string? PageId, string? Address);

/// <summary>Something a page is asked to do.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
/// <param name="PageId">The page.</param>
/// <param name="Command"><c>back</c>, <c>forward</c>, <c>reload</c>, <c>stop</c>, <c>devtools</c> or <c>focus</c>.</param>
internal sealed record BrowserCommandRequest(string? ExpectedEpoch, string? SessionId, string? PageId, string? Command);

/// <summary>The answer to the JavaScript dialog of a page.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
/// <param name="PageId">The page.</param>
/// <param name="Accept">Whether the dialog is accepted, or dismissed.</param>
/// <param name="Text">The text of a prompt that is accepted; null for its default text.</param>
internal sealed record BrowserDialogRequest(string? ExpectedEpoch, string? SessionId, string? PageId, bool Accept, string? Text);

/// <summary>Pick mode of a page.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
/// <param name="PageId">The page.</param>
/// <param name="On">Whether the mode is turned on.</param>
internal sealed record BrowserPickRequest(string? ExpectedEpoch, string? SessionId, string? PageId, bool On);

/// <summary>The text of an annotation to make of the element that was picked.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
/// <param name="Text">What the user wrote.</param>
internal sealed record BrowserAnnotationTextRequest(string? ExpectedEpoch, string? SessionId, string? Text);

/// <summary>A request about one annotation.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
/// <param name="Number">The number of the annotation; 0 names the element that was picked, for its picture.</param>
/// <param name="Action"><c>resolve</c>, <c>reopen</c>, <c>delete</c> or <c>reveal</c>, with <c>changeAnnotation</c>.</param>
internal sealed record BrowserAnnotationRequest(string? ExpectedEpoch, string? SessionId, int Number, string? Action = null);

/// <summary>An element of a page as the page reported it. Every text is text of the page.</summary>
/// <param name="Selector">A CSS selector that found it.</param>
/// <param name="Tag">Its tag.</param>
/// <param name="Role">Its role, or empty.</param>
/// <param name="Name">Its accessible name, or empty.</param>
/// <param name="Text">Its visible text, shortened.</param>
/// <param name="Html">Its opening tag.</param>
internal sealed record BrowserElementItem(string Selector, string Tag, string Role, string Name, string Text, string Html);

/// <summary>An annotation of a session.</summary>
/// <param name="Number">Its number within the session.</param>
/// <param name="Text">What the user wrote.</param>
/// <param name="Status"><c>open</c> or <c>resolved</c>.</param>
/// <param name="ResolvedBy"><c>user</c> or <c>agent</c>, when it is resolved.</param>
/// <param name="Note">What was said when it was resolved, or null.</param>
/// <param name="Address">The address of the page.</param>
/// <param name="Title">The title of the page.</param>
/// <param name="Element">The element as the page reported it.</param>
/// <param name="HasImage">Whether a picture of the element is kept.</param>
/// <param name="Found">Whether the element is on the page in front; null when the annotation is of another address or resolved.</param>
internal sealed record BrowserAnnotationItem(int Number, string Text, string Status, string? ResolvedBy, string? Note, string Address, string Title, BrowserElementItem Element, bool HasImage, bool? Found);

/// <summary>The element that was picked and waits for the text of the user.</summary>
/// <param name="PageId">The page it was picked in.</param>
/// <param name="Address">The address of that page.</param>
/// <param name="Title">The title of that page.</param>
/// <param name="Element">The element as the page reported it.</param>
/// <param name="HasImage">Whether a picture of the element was taken.</param>
internal sealed record BrowserPendingItem(string PageId, string Address, string Title, BrowserElementItem Element, bool HasImage);

/// <summary>The annotations of a session.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Annotations">Its annotations, the open ones first.</param>
/// <param name="PickingPageId">The page in which an element is being picked, or null.</param>
/// <param name="Pending">The element that was picked and waits for its text, or null.</param>
/// <param name="Open">How many annotations wait to be done.</param>
internal sealed record BrowserAnnotationsItem(string SessionId, BrowserAnnotationItem[] Annotations, string? PickingPageId, BrowserPendingItem? Pending, int Open);

/// <summary>The outcome of a request about the annotations of a session.</summary>
/// <param name="Status">
/// <c>ok</c>; <c>unavailable</c> or <c>stale_epoch</c>; <c>unsupported</c>, <c>invalid</c>, <c>closed</c> as for the
/// browser; <c>not_found</c> when the session has no such page or annotation; <c>no_pick</c> when no element waits
/// for a text; <c>limit</c> when the session has too many annotations; <c>busy</c> when the page waits in a
/// dialog; <c>failed</c> when the page did not answer.
/// </param>
/// <param name="Annotations">The annotations of the session after the request.</param>
internal sealed record BrowserAnnotationsReply(string Status, BrowserAnnotationsItem? Annotations = null);

/// <summary>The open annotations of a session as a prompt.</summary>
/// <param name="Status"><c>ok</c>; <c>not_found</c> when the session has no open annotation; otherwise as for the browser.</param>
/// <param name="Text">The prompt, with <c>ok</c>.</param>
/// <param name="Pictures">The numbers of the annotations whose picture goes with the prompt, in order.</param>
internal sealed record BrowserAnnotationPromptReply(string Status, string? Text, int[] Pictures);

/// <summary>Where the pane of a session shows its browser.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
/// <param name="Visible">Whether the browser of the session is shown; the place is not read otherwise.</param>
/// <param name="X">The distance from the left edge of the page of the application, in the pixels of the display.</param>
/// <param name="Y">The distance from its top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
internal sealed record BrowserPlaceRequest(string? ExpectedEpoch, string? SessionId, bool Visible, int X, int Y, int Width, int Height);

/// <summary>A request to be told about the browsers.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
internal sealed record BrowserWatchRequest(string? ExpectedEpoch);

/// <summary>A page of the browser of a session.</summary>
/// <param name="Id">Its identifier.</param>
/// <param name="Address">The address of its document.</param>
/// <param name="Title">The title of its document; empty when it has none.</param>
/// <param name="Loading">Whether a document is being loaded.</param>
/// <param name="CanGoBack">Whether its history has an earlier document.</param>
/// <param name="CanGoForward">Whether its history has a later document.</param>
/// <param name="Live">Whether the page has its view: a page without one loads its address again when it is shown.</param>
/// <param name="Dialog">The JavaScript dialog the page has open and waits in, or null.</param>
internal sealed record BrowserPageItem(string Id, string Address, string Title, bool Loading, bool CanGoBack, bool CanGoForward, bool Live, BrowserDialogItem? Dialog = null);

/// <summary>A JavaScript dialog of a page. Its texts are those of the page.</summary>
/// <param name="Kind"><c>alert</c>, <c>confirm</c>, <c>prompt</c> or <c>beforeunload</c>.</param>
/// <param name="Message">What the page says.</param>
/// <param name="DefaultText">The text a prompt starts with; null for the other kinds.</param>
internal sealed record BrowserDialogItem(string Kind, string Message, string? DefaultText);

/// <summary>The browser of a session.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Pages">Its pages, in the order of their strip.</param>
/// <param name="ActivePageId">The page in front, or null when there is none.</param>
internal sealed record BrowserSessionItem(string SessionId, BrowserPageItem[] Pages, string? ActivePageId);

/// <summary>The outcome of a request to the browser of a session.</summary>
/// <param name="Status">
/// <c>ok</c>; <c>unavailable</c> or <c>stale_epoch</c>; <c>unsupported</c> on a system without browser views;
/// <c>invalid</c> for an identifier, an address, a command or a place that cannot be taken; <c>not_found</c> when
/// the session has no such page; <c>limit</c> when it has too many pages; <c>failed</c> when the view of a page
/// could not be created; <c>closed</c> while the application exits.
/// </param>
/// <param name="Session">The browser of the session after the request, when it was looked at.</param>
/// <param name="PageId">The page that was opened, with <c>open</c>.</param>
internal sealed record BrowserReply(string Status, BrowserSessionItem? Session = null, string? PageId = null);

/// <summary>A picture of a page.</summary>
/// <param name="Status"><c>ok</c>; <c>unavailable</c> or <c>stale_epoch</c>; <c>unavailable_page</c> when the page has no view or could not be captured.</param>
/// <param name="Image">The picture as a <c>data:</c> address, with <c>ok</c>.</param>
/// <param name="Width">Its width, in pixels.</param>
/// <param name="Height">Its height, in pixels.</param>
internal sealed record BrowserCaptureReply(string Status, string? Image, int Width, int Height);

/// <summary>One thing the page is told about the browsers.</summary>
/// <param name="Kind">
/// <c>ready</c>: first, with whether this system has browser views. <c>session</c>: the browser of a session, as it
/// is now. <c>annotations</c>: the annotations of a session, as they are now.
/// </param>
/// <param name="Supported">Whether this system can show a browser inside the window.</param>
/// <param name="Session">The browser of a session, with <c>session</c>.</param>
/// <param name="Annotations">The annotations of a session, with <c>annotations</c>.</param>
internal sealed record BrowserEvent(string Kind, bool Supported, BrowserSessionItem? Session, BrowserAnnotationsItem? Annotations = null);
