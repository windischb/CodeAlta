using System.Net.NetworkInformation;
using System.Net.Sockets;
using CodeAlta.Desktop.Remote;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page's side of the remote server of the application: whether it runs, the address a browser opens,
/// how many remote windows are connected, the address it listens on, and the switches that change them.
/// </summary>
[NeoRpcService("remoteHost", Version = 1)]
internal sealed class RemoteHostService
{
    private readonly DesktopRemoteServer? _server;
    private readonly string? _epoch;
    private readonly Func<IReadOnlyList<RemoteHostAddress>> _addresses;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal RemoteHostService()
    {
        _addresses = static () => [];
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="server">The remote server of the application.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="addresses">The addresses the server can listen on; the network interfaces of this computer when null.</param>
    internal RemoteHostService(DesktopRemoteServer server, string epoch, Func<IReadOnlyList<RemoteHostAddress>>? addresses = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_server, _epoch) = (server, epoch);
        _addresses = addresses ?? Addresses;
    }

    /// <summary>Says what the server is doing.</summary>
    [NeoRpcMethod("status")]
    public RemoteHostResponse Status(RemoteHostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Refuse(request.ExpectedEpoch) is { } refused ? Refused(refused) : Describe();
    }

    /// <summary>Turns the server on or off, now and for the next starts, and says what it is doing then.</summary>
    [NeoRpcMethod("setEnabled", TimeoutMilliseconds = 30_000)]
    public async Task<RemoteHostResponse> SetEnabledAsync(RemoteHostEnableRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return Refused(refused);
        // The server finishes what it started whatever becomes of this call.
        await _server!.SetEnabledAsync(request.Enabled).WaitAsync(cancellationToken).ConfigureAwait(false);
        return Describe();
    }

    /// <summary>Changes the address the server listens on, now and for the next starts; a running server listens again there.</summary>
    [NeoRpcMethod("setBind", TimeoutMilliseconds = 30_000)]
    public async Task<RemoteHostResponse> SetBindAsync(RemoteHostBindRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return Refused(refused);
        if (!DesktopCommandLine.IsMcpHost(request.Host)) return Refused("invalid_request");
        await _server!.SetBindAsync(request.Host!).WaitAsync(cancellationToken).ConfigureAwait(false);
        return Describe();
    }

    /// <summary>
    /// The addresses this computer can listen on: the loopback address first, the IPv4 address of each network
    /// interface that is up, with the name of the interface, and every address last.
    /// </summary>
    internal static IReadOnlyList<RemoteHostAddress> Addresses()
    {
        var addresses = new List<RemoteHostAddress> { new(DesktopRemoteEndpoint.DefaultHost, "") };
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    addresses.Add(new(unicast.Address.ToString(), network.Name));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // The interfaces cannot be listed: the loopback address and every address remain.
        }

        addresses.Add(new("0.0.0.0", ""));
        return addresses;
    }

    private RemoteHostResponse Describe()
    {
        var status = _server!.Status;
        return new RemoteHostResponse("ok", status.State, status.Enabled, status.Url, status.Error, status.Clients, status.Bind, [.. _addresses()]);
    }

    private static RemoteHostResponse Refused(string status) => new(status, DesktopRemoteStatus.Stopped, false, null, null, 0, DesktopRemoteEndpoint.DefaultHost, []);

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

/// <summary>The request that changes the address the remote server listens on.</summary>
/// <param name="ExpectedEpoch">The host epoch the page was given.</param>
/// <param name="Host"><c>localhost</c> or an IP address; <c>0.0.0.0</c> for every address.</param>
internal sealed record RemoteHostBindRequest(string? ExpectedEpoch, string? Host);

/// <summary>An address the remote server can listen on.</summary>
/// <param name="Host">The address.</param>
/// <param name="Name">The name of the network interface it belongs to; empty for the loopback address and for every address.</param>
internal sealed record RemoteHostAddress(string Host, string Name);

/// <summary>What the remote server of the application is doing.</summary>
/// <param name="Status"><c>ok</c>, <c>unavailable</c> (this launch has no server), <c>stale_epoch</c> or <c>invalid_request</c>.</param>
/// <param name="State"><c>running</c>, <c>stopped</c> (turned off) or <c>failed</c> (turned on, and not listening).</param>
/// <param name="Enabled">Whether the server is turned on.</param>
/// <param name="Url">The address a browser opens while it runs.</param>
/// <param name="Error">Why it is not listening, when it failed.</param>
/// <param name="Clients">How many remote windows are connected.</param>
/// <param name="Bind">The address the server listens on.</param>
/// <param name="Addresses">The addresses it can listen on.</param>
internal sealed record RemoteHostResponse(string Status, string State, bool Enabled, string? Url, string? Error, int Clients, string Bind, RemoteHostAddress[] Addresses);
