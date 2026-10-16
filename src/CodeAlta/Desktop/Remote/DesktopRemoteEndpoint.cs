using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CodeAlta.Desktop.Mcp;

namespace CodeAlta.Desktop.Remote;

/// <summary>Where the remote server of the application listens.</summary>
/// <param name="Host"><c>localhost</c> or an IP address.</param>
/// <param name="Port">The port; 0 for a free one.</param>
/// <param name="Chosen">
/// Whether the user named the port for this start. A port of the application's own choice that is taken is
/// replaced by a free one; one the user named is not.
/// </param>
internal sealed record DesktopRemoteEndpoint(string Host, int Port, bool Chosen)
{
    /// <summary>The address the server listens on when none is named: this computer only.</summary>
    internal const string DefaultHost = DesktopMcpEndpoint.DefaultHost;

    /// <summary>The port of the normal instance, next to the MCP server's.</summary>
    internal const int DefaultPort = 2584;

    /// <summary>The port of the developer instance, which runs beside the normal one.</summary>
    internal const int DeveloperPort = 2585;

    /// <summary>
    /// The endpoint of a start: what its options name, or else the loopback address and the port of the
    /// instance. An instance on explicit roots is a test or automation, several of which run at once: it takes
    /// a free port.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    internal static DesktopRemoteEndpoint For(DesktopLaunchOptions options) => For(options, null);

    /// <summary>
    /// The endpoint of a start, with the address the user kept in the preferences when the options name none.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    internal static DesktopRemoteEndpoint For(DesktopLaunchOptions options, string? preferredHost)
    {
        ArgumentNullException.ThrowIfNull(options);
        var port = options.RemotePort ?? (options.Owned is not { Home: null } ? 0 : options.Developer ? DeveloperPort : DefaultPort);
        var host = options.RemoteHost ?? (DesktopCommandLine.IsMcpHost(preferredHost) ? preferredHost! : DefaultHost);
        return new DesktopRemoteEndpoint(host, port, options.RemotePort is not null);
    }

    /// <summary>The same address with another port.</summary>
    internal DesktopRemoteEndpoint On(int port) => this with { Port = port };

    /// <summary>Whether only programs of this computer reach the address.</summary>
    internal bool IsLoopback => DesktopMcpEndpoint.IsLoopbackHost(Host);

    /// <summary>The address of the page as a browser names it.</summary>
    internal string Url
    {
        get
        {
            var host = Host;
            if (IPAddress.TryParse(host, out var address))
            {
                // A server on every address is reached by the name of the computer.
                if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) host = Dns.GetHostName();
                else if (address.AddressFamily == AddressFamily.InterNetworkV6) host = "[" + address + "]";
            }

            return "http://" + host + ":" + Port.ToString(CultureInfo.InvariantCulture) + "/";
        }
    }
}
