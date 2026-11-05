using System.Runtime.CompilerServices;
using CodeAlta.Desktop.Browser;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page's side of the built-in browser of the sessions: it opens, closes and orders the browser tabs of a
/// session, sends them to an address, and says where its window shows the browser of the session. The browser tabs
/// are native views that the host owns: the page of the application draws the frame around them and never their
/// content.
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

    /// <summary>The browser of a session, with the browser tabs kept for it from an earlier run.</summary>
    /// <param name="request">The session.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    [NeoRpcMethod("state")]
    public async Task<BrowserReply> StateAsync(BrowserSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.GetAsync(request.SessionId ?? string.Empty).ConfigureAwait(false));
    }

    /// <summary>Opens a browser tab of a session and puts it in front.</summary>
    /// <param name="request">The session and the address.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    [NeoRpcMethod("open")]
    public async Task<BrowserReply> OpenAsync(BrowserOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.OpenAsync(request.SessionId ?? string.Empty, request.Address).ConfigureAwait(false));
    }

    /// <summary>Closes a browser tab.</summary>
    /// <param name="request">The session and the browser tab.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    [NeoRpcMethod("close")]
    public async Task<BrowserReply> CloseAsync(BrowserPageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.CloseAsync(request.SessionId ?? string.Empty, request.PageId ?? string.Empty).ConfigureAwait(false));
    }

    /// <summary>Puts a browser tab in front.</summary>
    /// <param name="request">The session and the browser tab.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    [NeoRpcMethod("select")]
    public async Task<BrowserReply> SelectAsync(BrowserPageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.SelectAsync(request.SessionId ?? string.Empty, request.PageId ?? string.Empty).ConfigureAwait(false));
    }

    /// <summary>Moves a browser tab to another place of the strip.</summary>
    /// <param name="request">The session, the browser tab and its new place.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    [NeoRpcMethod("move")]
    public async Task<BrowserReply> MoveAsync(BrowserMoveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.MoveAsync(request.SessionId ?? string.Empty, request.PageId ?? string.Empty, request.Index).ConfigureAwait(false));
    }

    /// <summary>Loads an address in a browser tab: what was typed, read as <see cref="BrowserAddress.Parse"/> reads it.</summary>
    /// <param name="request">The session, the browser tab and the address.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    [NeoRpcMethod("navigate")]
    public async Task<BrowserReply> NavigateAsync(BrowserNavigateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return Reply(await _browsers!.NavigateAsync(request.SessionId ?? string.Empty, request.PageId ?? string.Empty, request.Address).ConfigureAwait(false));
    }

    /// <summary>Asks a browser tab to go back or forward, to load again, to stop, or to take the keyboard.</summary>
    /// <param name="request">The session, the browser tab and the command.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
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
            "focus" => BrowserCommand.Focus,
            _ => null,
        };
        if (command is null) return new("invalid");
        return Reply(await _browsers!.CommandAsync(request.SessionId ?? string.Empty, request.PageId ?? string.Empty, command.Value).ConfigureAwait(false));
    }

    /// <summary>
    /// Says where the window that calls shows the browser of a session, in the CSS pixels of its page from its
    /// top-left corner, or that it no longer shows it.
    /// </summary>
    /// <param name="request">The session and the place.</param>
    /// <param name="context">The call, which names the window.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    [NeoRpcMethod("place")]
    public async Task<BrowserReply> PlaceAsync(BrowserPlaceRequest request, NeoRpcContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        var window = context.ViewLabel ?? string.Empty;
        var placement = request.Visible ? new BrowserPlacement(window, request.X, request.Y, request.Width, request.Height) : null;
        return Reply(await _browsers!.PlaceAsync(request.SessionId ?? string.Empty, window, placement).ConfigureAwait(false));
    }

    /// <summary>A picture of what a browser tab shows, as a <c>data:</c> address: the still image shown while something of the page is above it.</summary>
    /// <param name="request">The session and the browser tab.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
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
    /// <param name="request">The epoch.</param>
    /// <param name="cancellationToken">Ends the watch.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
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
        await foreach (var session in _browsers.WatchAsync(cancellationToken).ConfigureAwait(false)) yield return new BrowserEvent("session", true, Item(session));
    }

    private string? Refuse(string? expectedEpoch)
        => _browsers is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    private static BrowserReply Reply(BrowserResult result) => new(result.Status, result.Session is null ? null : Item(result.Session), result.PageId);

    private static BrowserSessionItem Item(BrowserSessionInfo session)
        => new(session.SessionId,
            [.. session.Pages.Select(static page => new BrowserPageItem(page.Id, page.Address, page.Title, page.Loading, page.CanGoBack, page.CanGoForward, page.Live))],
            session.ActivePageId, session.Window);
}

/// <summary>A request about the browser of a session.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
internal sealed record BrowserSessionRequest(string? ExpectedEpoch, string? SessionId);

/// <summary>A browser tab to open.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
/// <param name="Address">What was typed as its address; null or empty for the empty page.</param>
internal sealed record BrowserOpenRequest(string? ExpectedEpoch, string? SessionId, string? Address);

/// <summary>A request about one browser tab.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
/// <param name="PageId">The browser tab.</param>
internal sealed record BrowserPageRequest(string? ExpectedEpoch, string? SessionId, string? PageId);

/// <summary>A browser tab to move in the strip.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
/// <param name="PageId">The browser tab.</param>
/// <param name="Index">Its new place, from 0.</param>
internal sealed record BrowserMoveRequest(string? ExpectedEpoch, string? SessionId, string? PageId, int Index);

/// <summary>An address to load in a browser tab.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
/// <param name="PageId">The browser tab.</param>
/// <param name="Address">What was typed as the address.</param>
internal sealed record BrowserNavigateRequest(string? ExpectedEpoch, string? SessionId, string? PageId, string? Address);

/// <summary>A command for a browser tab.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
/// <param name="PageId">The browser tab.</param>
/// <param name="Command"><c>back</c>, <c>forward</c>, <c>reload</c>, <c>stop</c> or <c>focus</c>.</param>
internal sealed record BrowserCommandRequest(string? ExpectedEpoch, string? SessionId, string? PageId, string? Command);

/// <summary>Where the window that calls shows the browser of a session.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="SessionId">The session.</param>
/// <param name="Visible">Whether the window shows it; false when it no longer does.</param>
/// <param name="X">The distance from the left edge of the page, in CSS pixels.</param>
/// <param name="Y">The distance from the top edge of the page, in CSS pixels.</param>
/// <param name="Width">The width, in CSS pixels.</param>
/// <param name="Height">The height, in CSS pixels.</param>
internal sealed record BrowserPlaceRequest(string? ExpectedEpoch, string? SessionId, bool Visible, double X, double Y, double Width, double Height);

/// <summary>A watch of the browsers.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
internal sealed record BrowserWatchRequest(string? ExpectedEpoch);

/// <summary>A browser tab of a session.</summary>
/// <param name="Id">The identifier.</param>
/// <param name="Address">The address of its document.</param>
/// <param name="Title">The title of its document.</param>
/// <param name="Loading">Whether it loads.</param>
/// <param name="CanGoBack">Whether its history has an earlier document.</param>
/// <param name="CanGoForward">Whether its history has a later document.</param>
/// <param name="Live">Whether it has its view.</param>
internal sealed record BrowserPageItem(string Id, string Address, string Title, bool Loading, bool CanGoBack, bool CanGoForward, bool Live);

/// <summary>The browser of a session.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Pages">Its browser tabs, in the order of their strip.</param>
/// <param name="ActivePageId">The browser tab in front.</param>
/// <param name="Window">The label of the view of the window that shows the browser, or null.</param>
internal sealed record BrowserSessionItem(string SessionId, BrowserPageItem[] Pages, string? ActivePageId, string? Window);

/// <summary>The answer to a request about the browser of a session.</summary>
/// <param name="Status">What happened: see <see cref="BrowserResult.Status"/>, or <c>unavailable</c>, <c>stale_epoch</c>.</param>
/// <param name="Session">The browser of the session after the request.</param>
/// <param name="PageId">The browser tab a request opened.</param>
internal sealed record BrowserReply(string Status, BrowserSessionItem? Session = null, string? PageId = null);

/// <summary>A picture of a browser tab.</summary>
/// <param name="Status"><c>ok</c>, <c>unavailable_page</c>, <c>unavailable</c> or <c>stale_epoch</c>.</param>
/// <param name="Image">The picture as a <c>data:</c> address.</param>
/// <param name="Width">Its width in pixels.</param>
/// <param name="Height">Its height in pixels.</param>
internal sealed record BrowserCaptureReply(string Status, string? Image, int Width, int Height);

/// <summary>What the watch of the browsers tells.</summary>
/// <param name="Kind"><c>ready</c>, then <c>session</c> for each change.</param>
/// <param name="Supported">Whether this system has the built-in browser.</param>
/// <param name="Session">The browser of a session that changed.</param>
internal sealed record BrowserEvent(string Kind, bool Supported, BrowserSessionItem? Session);
