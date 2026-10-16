using CodeAlta.Desktop.Remote;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page's side of the remote server of the application: whether it runs, the address a browser opens,
/// how many remote windows are connected, and the switch that turns it on and off.
/// </summary>
[NeoRpcService("remoteHost", Version = 1)]
internal sealed class RemoteHostService
{
    private readonly DesktopRemoteServer? _server;
    private readonly string? _epoch;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal RemoteHostService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="server">The remote server of the application.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    internal RemoteHostService(DesktopRemoteServer server, string epoch)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_server, _epoch) = (server, epoch);
    }

    /// <summary>Says what the server is doing.</summary>
    [NeoRpcMethod("status")]
    public RemoteHostResponse Status(RemoteHostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Refuse(request.ExpectedEpoch) is { } refused ? Refused(refused) : Describe();
    }

    /// <summary>Turns the server on or off, now, and says what it is doing then.</summary>
    [NeoRpcMethod("setEnabled", TimeoutMilliseconds = 30_000)]
    public async Task<RemoteHostResponse> SetEnabledAsync(RemoteHostEnableRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return Refused(refused);
        // The server finishes what it started whatever becomes of this call.
        await _server!.SetEnabledAsync(request.Enabled).WaitAsync(cancellationToken).ConfigureAwait(false);
        return Describe();
    }

    private RemoteHostResponse Describe()
    {
        var status = _server!.Status;
        return new RemoteHostResponse("ok", status.State, status.Enabled, status.Url, status.Error, status.Clients);
    }

    private static RemoteHostResponse Refused(string status) => new(status, DesktopRemoteStatus.Stopped, false, null, null, 0);

    private string? Refuse(string? expectedEpoch)
        => _server is null ? "unavailable"
            : !string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? "stale_epoch" : null;
}

/// <summary>A request about the remote server of the application.</summary>
/// <param name="ExpectedEpoch">The host epoch the page was given.</param>
internal sealed record RemoteHostRequest(string? ExpectedEpoch);

/// <summary>The request that turns the remote server on or off.</summary>
/// <param name="ExpectedEpoch">The host epoch the page was given.</param>
/// <param name="Enabled">Whether the server is to run.</param>
internal sealed record RemoteHostEnableRequest(string? ExpectedEpoch, bool Enabled);

/// <summary>What the remote server of the application is doing.</summary>
/// <param name="Status"><c>ok</c>, <c>unavailable</c> (this launch has no server) or <c>stale_epoch</c>.</param>
/// <param name="State"><c>running</c>, <c>stopped</c> (turned off) or <c>failed</c> (turned on, and not listening).</param>
/// <param name="Enabled">Whether the server is turned on.</param>
/// <param name="Url">The address a browser opens while it runs.</param>
/// <param name="Error">Why it is not listening, when it failed.</param>
/// <param name="Clients">How many remote windows are connected.</param>
internal sealed record RemoteHostResponse(string Status, string State, bool Enabled, string? Url, string? Error, int Clients);
