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

    /// <summary>
    /// The browsers as they change: first whether this system has them, then each session whose browser was asked
    /// for, and a session again each time its browser changes.
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
        await foreach (var session in _browsers.WatchAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new BrowserEvent("session", true, Item(session));
        }
    }

    private string? Refuse(string? expectedEpoch)
        => _browsers is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    private static BrowserReply Reply(BrowserResult result) => new(result.Status, result.Session is null ? null : Item(result.Session), result.PageId);

    private static BrowserSessionItem Item(BrowserSessionInfo session)
        => new(session.SessionId,
            [.. session.Pages.Select(static page => new BrowserPageItem(page.Id, page.Address, page.Title, page.Loading, page.CanGoBack, page.CanGoForward, page.Live))],
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
internal sealed record BrowserPageItem(string Id, string Address, string Title, bool Loading, bool CanGoBack, bool CanGoForward, bool Live);

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
/// <param name="Kind"><c>ready</c>: first, with whether this system has browser views. <c>session</c>: the browser of a session, as it is now.</param>
/// <param name="Supported">Whether this system can show a browser inside the window.</param>
/// <param name="Session">The browser of a session, with <c>session</c>.</param>
internal sealed record BrowserEvent(string Kind, bool Supported, BrowserSessionItem? Session);
