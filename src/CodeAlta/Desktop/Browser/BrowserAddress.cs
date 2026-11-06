namespace CodeAlta.Desktop.Browser;

/// <summary>
/// The addresses the browser of a session loads: <c>http</c> and <c>https</c>, and the empty page. Nothing else is
/// loaded, whoever asks: a file of the disk, the application's own scheme and a script are not pages of the web.
/// </summary>
internal static class BrowserAddress
{
    /// <summary>The address of the empty page.</summary>
    internal const string Blank = "about:blank";

    /// <summary>The longest address that is loaded, in UTF-16 units.</summary>
    internal const int MaximumLength = 4096;

    /// <summary>
    /// Reads what was typed as an address. A name without a scheme is taken as a host: <c>http</c> for this
    /// computer (<c>localhost</c>, an address of the loopback or of a private network, a name without a dot), and
    /// <c>https</c> for everything else.
    /// </summary>
    /// <param name="text">What was typed.</param>
    /// <returns>The address to load, or null when the text names no page the browser loads.</returns>
    internal static Uri? Parse(string? text)
    {
        if (text is null) return null;
        text = text.Trim();
        if (text.Length is 0 or > MaximumLength || text.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character))) return null;
        if (string.Equals(text, Blank, StringComparison.OrdinalIgnoreCase)) return new Uri(Blank);
        // "localhost:5173" parses as an address of the scheme "localhost": a scheme is only what is followed by "//".
        if (text.Contains("://", StringComparison.Ordinal)) return Uri.TryCreate(text, UriKind.Absolute, out var given) && IsAllowed(given) ? given : null;
        if (!Uri.TryCreate("http://" + text, UriKind.Absolute, out var guessed) || guessed.Host.Length == 0) return null;
        return IsLocal(guessed.Host) ? guessed : Uri.TryCreate("https://" + text, UriKind.Absolute, out var secure) ? secure : null;
    }

    /// <summary>Whether the browser loads an address: an absolute <c>http</c> or <c>https</c> address with a host, or the empty page.</summary>
    /// <param name="address">The address.</param>
    internal static bool IsAllowed(Uri? address)
    {
        if (address is null || !address.IsAbsoluteUri) return false;
        if (address.Scheme == "about") return string.Equals(address.OriginalString, Blank, StringComparison.OrdinalIgnoreCase) || address.AbsolutePath == "blank";
        return (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps) && address.Host.Length > 0 && address.AbsoluteUri.Length <= MaximumLength;
    }

    /// <summary>
    /// Whether an address is a port of CodeAlta itself on this computer, which no page of the browser reaches: its MCP
    /// server and the one of the developer instance.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="reservedPorts">The ports of CodeAlta.</param>
    internal static bool IsReserved(Uri? address, IReadOnlyCollection<int> reservedPorts)
    {
        ArgumentNullException.ThrowIfNull(reservedPorts);
        if (address is null || !address.IsAbsoluteUri || address.Port < 0 || !reservedPorts.Contains(address.Port)) return false;
        var host = address.Host.Trim('[', ']');
        return address.IsLoopback || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || (System.Net.IPAddress.TryParse(host, out var ip) && (System.Net.IPAddress.IsLoopback(ip) || ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any)));
    }

    // A host that is reached without TLS as a rule: this computer, a private network, a name of the local network.
    private static bool IsLocal(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            if (System.Net.IPAddress.IsLoopback(address)) return true;
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254);
        }

        return !host.Contains('.');
    }
}
