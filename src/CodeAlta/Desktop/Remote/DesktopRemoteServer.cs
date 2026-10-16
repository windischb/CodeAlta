using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CodeAlta.Desktop.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NeoAstra;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Remote;

/// <summary>What the remote server is doing.</summary>
/// <param name="State"><c>running</c>, <c>stopped</c> (turned off) or <c>failed</c> (turned on, and not listening).</param>
/// <param name="Enabled">Whether the server is turned on.</param>
/// <param name="Url">The address a browser opens while it runs.</param>
/// <param name="Error">Why it is not listening, when it failed.</param>
/// <param name="Clients">How many remote windows are connected.</param>
internal sealed record DesktopRemoteStatus(string State, bool Enabled, string? Url, string? Error, int Clients)
{
    internal const string Running = "running";
    internal const string Stopped = "stopped";
    internal const string Failed = "failed";
}

/// <summary>
/// Serves the page of the application to a browser on another computer, with its RPC over a WebSocket: the
/// same <see cref="NeoRpcHost"/> the window uses, one document session per connection.
/// </summary>
/// <remarks>
/// <para>
/// The transport handshake that NeoAstra does for a WebView is done here: the client sends <c>hello</c>, the
/// server answers <c>hello_ack</c>, and every other frame is passed to the session as is. The page gets the
/// module that installs the transport object <c>@neoastra/client</c> looks for (<c>lib/remote.js</c> of the
/// frontend build), inserted into the entry document before its own module script, so the frontend runs
/// unchanged.
/// </para>
/// <para>
/// There is no authentication yet: whoever reaches the port has the user's control of the application. The
/// server listens on the loopback address unless a start names another one, and refuses a request whose
/// <c>Host</c> or <c>Origin</c> is not this computer (<see cref="DesktopMcpGuard"/>), as the MCP server does.
/// </para>
/// </remarks>
internal sealed class DesktopRemoteServer : IAsyncDisposable
{
    /// <summary>The path of the WebSocket endpoint that carries the RPC frames.</summary>
    internal const string RpcPath = "/rpc";

    /// <summary>The module of the frontend build that installs the transport in the page.</summary>
    internal const string TransportModule = "lib/remote.js";

    /// <summary>The label of the view every remote document session has; the page reads it to know it is remote.</summary>
    internal const string ViewLabel = "remote";

    /// <summary>The largest inbound frame, the same as the window's.</summary>
    internal const int MaximumFrameBytes = 128 * 1024 * 1024;

    /// <summary>How many remote windows are served at once.</summary>
    internal const int MaximumConnections = 8;

    private const int MaximumJsonDepth = 32;
    private static readonly string[] Features = ["invoke", "cancel", "events"];

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly DesktopRemoteEndpoint _endpoint;
    private readonly Func<NeoRpcHost?> _rpc;
    private readonly INeoResourceProvider _assets;
    private readonly string _origin;
    private int _connections;
    private WebApplication? _application;
    private DesktopRemoteStatus _status;
    private bool _disposed;

    /// <summary>Creates the server. It does not listen until <see cref="StartAsync"/>.</summary>
    /// <param name="endpoint">Where it listens.</param>
    /// <param name="rpc">The RPC host of the window; null while the application has none yet.</param>
    /// <param name="assets">The files of the page, as the window loads them.</param>
    /// <param name="origin">The origin the window's page is served from (<c>app://codealta</c>), which the asset provider answers.</param>
    /// <param name="enabled">Whether the server is turned on.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    internal DesktopRemoteServer(DesktopRemoteEndpoint endpoint, Func<NeoRpcHost?> rpc, INeoResourceProvider assets, string origin, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        _endpoint = endpoint;
        _rpc = rpc;
        _assets = assets;
        _origin = origin.TrimEnd('/');
        _status = new DesktopRemoteStatus(DesktopRemoteStatus.Stopped, enabled, null, null, 0);
    }

    /// <summary>Gets what the server is doing.</summary>
    internal DesktopRemoteStatus Status { get { lock (_gate) return _status; } }

    /// <summary>Raised when <see cref="Status"/> changed.</summary>
    internal event Action? Changed;

    /// <summary>Starts listening when the server is turned on. A failure is kept in <see cref="Status"/>.</summary>
    internal async Task StartAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            if (Status.Enabled) await StartCoreAsync().ConfigureAwait(false);
        }
        finally { _transition.Release(); }
    }

    /// <summary>Turns the server on or off, now.</summary>
    internal async Task SetEnabledAsync(bool enabled)
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            if (enabled)
            {
                Set(Status with { Enabled = true });
                if (_application is null) await StartCoreAsync().ConfigureAwait(false);
            }
            else
            {
                await StopCoreAsync().ConfigureAwait(false);
                Set(new DesktopRemoteStatus(DesktopRemoteStatus.Stopped, false, null, null, 0));
            }
        }
        finally { _transition.Release(); }
    }

    /// <summary>Stops listening for good.</summary>
    public async ValueTask DisposeAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { _transition.Release(); }
    }

    private async Task StartCoreAsync()
    {
        try
        {
            WebApplication application;
            try
            {
                application = await ListenAsync(_endpoint.Port).ConfigureAwait(false);
            }
            catch (IOException) when (_endpoint.Port != 0 && !_endpoint.Chosen)
            {
                // The port of the application's own choice is taken, by another program or another user's CodeAlta.
                application = await ListenAsync(0).ConfigureAwait(false);
            }

            _application = application;
            var url = _endpoint.On(BoundPort(application)).Url;
            Info($"The remote server listens at {url}");
            Set(new DesktopRemoteStatus(DesktopRemoteStatus.Running, true, url, null, 0));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException
            or System.Net.Sockets.SocketException or FormatException)
        {
            Warn($"The remote server could not listen on {_endpoint.Host}:{_endpoint.Port}: {exception.Message}");
            Set(new DesktopRemoteStatus(DesktopRemoteStatus.Failed, true, null, exception.Message, 0));
        }
    }

    private async Task StopCoreAsync()
    {
        var application = _application;
        _application = null;
        if (application is null) return;
        try
        {
            // The windows that are connected get a moment to be told; one that holds on does not keep the application.
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await application.StopAsync(patience.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException)
        {
            Warn($"The remote server did not stop cleanly: {exception.Message}");
        }

        await application.DisposeAsync().ConfigureAwait(false);
    }

    // A server of its own, as the MCP server is: nothing read from the folder the application was started in,
    // no console and no signal of the process decides where it listens or when it stops.
    private async Task<WebApplication> ListenAsync(int port)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ApplicationName = "CodeAlta", ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            if (string.Equals(_endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                // Both loopback addresses, except for a free port, which the two would not share.
                if (port == 0) kestrel.Listen(IPAddress.Loopback, 0);
                else kestrel.ListenLocalhost(port);
            }
            else kestrel.Listen(IPAddress.Parse(_endpoint.Host), port);
        });
        builder.Services.AddSingleton<IHostLifetime, QuietLifetime>();
        var application = builder.Build();
        application.UseWebSockets();
        application.Use(async (context, next) =>
        {
            var status = DesktopMcpGuard.Check(context.Request.Host.Host, context.Request.Headers.Origin, null, null);
            if (status != DesktopMcpGuard.Allowed)
            {
                context.Response.StatusCode = status;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        application.Run(HandleAsync);
        try
        {
            await application.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return application;
    }

    private async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (path == RpcPath)
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status426UpgradeRequired; return; }
            var rpc = _rpc();
            if (rpc is null) { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
            if (Interlocked.Increment(ref _connections) > MaximumConnections)
            {
                Interlocked.Decrement(ref _connections);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                await ServeAsync(rpc, socket, context.RequestAborted).ConfigureAwait(false);
            }
            finally { Interlocked.Decrement(ref _connections); CountClients(); }
            return;
        }

        if (context.Request.Method != HttpMethods.Get && context.Request.Method != HttpMethods.Head) { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        if (path == "/favicon.ico") { context.Response.StatusCode = StatusCodes.Status204NoContent; return; } // The page has no icon file; browsers ask anyway.
        if (path == "/") path = "/index.html";
        var response = _assets.GetResponse(new NeoResourceRequest(new Uri(_origin + path), context.Request.Method, new Dictionary<string, string>(), null,
            path.EndsWith(".html", StringComparison.Ordinal) ? NeoResourceKind.Document : NeoResourceKind.Other, path == "/index.html", ReadOnlyMemory<byte>.Empty));
        if (response is null || response.StatusCode != 200) { context.Response.StatusCode = response?.StatusCode ?? StatusCodes.Status404NotFound; return; }
        context.Response.ContentType = response.MimeType ?? "application/octet-stream";
        if (path == "/index.html")
        {
            var document = response.FilePath is { } file ? await File.ReadAllTextAsync(file, context.RequestAborted).ConfigureAwait(false) : Encoding.UTF8.GetString(response.Bytes.Span);
            await context.Response.WriteAsync(WithTransport(document), context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (response.FilePath is { } filePath) await context.Response.SendFileAsync(filePath, context.RequestAborted).ConfigureAwait(false);
        else await context.Response.Body.WriteAsync(response.Bytes, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// The entry document with the transport module before the page's own module script: module scripts run
    /// in the order of the document, so the transport is installed when the page connects.
    /// </summary>
    internal static string WithTransport(string document)
    {
        ArgumentNullException.ThrowIfNull(document);
        const string tag = "<script type=\"module\" src=\"./" + TransportModule + "\"></script>";
        var place = document.IndexOf("<script type=\"module\"", StringComparison.Ordinal);
        return place < 0 ? document.Replace("</head>", tag + "</head>", StringComparison.Ordinal) : document.Insert(place, tag);
    }

    /// <summary>The transport handshake, then the frames of the session, until either side closes.</summary>
    private async Task ServeAsync(NeoRpcHost rpc, WebSocket socket, CancellationToken cancellationToken)
    {
        var hello = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
        if (hello is null) return;
        using (var document = JsonDocument.Parse(hello))
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kind) || kind.GetString() != "hello"
                || !root.TryGetProperty("protocol", out var protocol) || !protocol.TryGetProperty("major", out var major) || major.GetInt32() != 1)
            {
                await SendAsync(socket, Frame("close", ("code", "protocol_mismatch")), cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        var sender = new SemaphoreSlim(1, 1);
        var documentSessionId = Guid.NewGuid().ToString("N");
        var identity = new NeoRpcSessionIdentity(ViewLabel, documentSessionId)
        {
            ContractHash = NeoRpcGeneratedContract.Hash, SourceOrigin = new Uri(Status.Url ?? _endpoint.Url), IsMainFrame = true, WholeViewTrust = false,
            ProtocolMinor = 0, Features = [.. Features],
        };
        await using var session = rpc.OpenSession(identity, async (json, token) =>
        {
            await sender.WaitAsync(token).ConfigureAwait(false);
            try { await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, token).ConfigureAwait(false); }
            finally { sender.Release(); }
        });
        await sender.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SendAsync(socket, HelloAck(documentSessionId), cancellationToken).ConfigureAwait(false); }
        finally { sender.Release(); }
        Info($"A remote window connected ({documentSessionId})");
        CountClients();
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Closed);
        try
        {
            while (!closing.IsCancellationRequested)
            {
                var json = await ReceiveAsync(socket, closing.Token).ConfigureAwait(false);
                if (json is null) break;
                if (IsClose(json)) break;
                // An invoke completes with its result: frames are not waited for one after the other.
                _ = ReceiveFrameAsync(session, json, closing.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException exception) { Info($"A remote window went away: {exception.Message}"); }
        if (session.Closed.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            // The host closed the session (it is being disposed): the page reloads to open a new one, as it does in the window.
            try { await SendAsync(socket, Frame("close", ("reason", "rpc_session_closed")), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            // The close handshake: answered at once when the page closed first; otherwise the page gets the close
            // and a moment to answer it, and a page that does not is not waited for.
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                if (socket.State == WebSocketState.CloseReceived) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", patience.Token).ConfigureAwait(false);
                else await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", patience.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        }

        Info($"A remote window disconnected ({documentSessionId})");
    }

    private static async Task ReceiveFrameAsync(NeoRpcSession session, string json, CancellationToken cancellationToken)
    {
        try { await session.ReceiveAsync(json, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            Warn($"A frame of a remote window was refused: {exception.Message}");
        }
    }

    private static bool IsClose(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("kind", out var kind) && kind.GetString() == "close";
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// The answer to <c>hello</c>, as <c>@neoastra/client</c> reads it: the protocol, the features, the runtime
    /// (whose platform and backend must be a true pair of the host's) and the limits of the session.
    /// </summary>
    internal static string HelloAck(string documentSessionId)
    {
        var (platform, backend) = OperatingSystem.IsWindows() ? ("windows", "webview2") : OperatingSystem.IsMacOS() ? ("macos", "wkwebview") : ("linux", "webkitgtk");
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("neoastra", 1);
            writer.WriteString("kind", "hello_ack");
            writer.WriteStartObject("protocol");
            writer.WriteNumber("major", 1);
            writer.WriteNumber("minor", 0);
            writer.WriteEndObject();
            writer.WriteStartArray("features");
            foreach (var feature in Features) writer.WriteStringValue(feature);
            writer.WriteEndArray();
            writer.WriteStartObject("runtime");
            writer.WriteString("viewLabel", ViewLabel);
            writer.WriteString("documentSessionId", documentSessionId);
            writer.WriteString("platform", platform);
            writer.WriteString("backend", backend);
            writer.WriteBoolean("wholeViewTrust", false);
            writer.WriteEndObject();
            writer.WriteStartObject("limits");
            writer.WriteNumber("maximumFrameBytes", MaximumFrameBytes);
            writer.WriteNumber("maximumJsonDepth", MaximumJsonDepth);
            writer.WriteNumber("maximumDiagnosticQueue", 100);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Frame(string kind, params (string Name, string Value)[] fields)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("neoastra", 1);
            writer.WriteString("kind", kind);
            foreach (var (name, value) in fields) writer.WriteString(name, value);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static Task SendAsync(WebSocket socket, string json, CancellationToken cancellationToken)
        => socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken);

    /// <summary>One text message, whole; null once the socket is closed or a message is not text.</summary>
    private static async Task<string?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) return null;
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaximumFrameBytes) return null;
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    private static int BoundPort(WebApplication application)
    {
        foreach (var address in application.Urls)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0) return uri.Port;
        }

        throw new InvalidOperationException("The server did not report the port it listens on.");
    }

    // The windows connected, as the status says it; a change while the server stops is not one.
    private void CountClients()
    {
        var clients = Math.Max(0, Volatile.Read(ref _connections));
        lock (_gate)
        {
            if (_status.State != DesktopRemoteStatus.Running || _status.Clients == clients) return;
            _status = _status with { Clients = clients };
        }

        Changed?.Invoke();
    }

    private void Set(DesktopRemoteStatus status)
    {
        lock (_gate)
        {
            if (_status == status) return;
            _status = status;
        }

        Changed?.Invoke();
    }

    private static void Info(string message)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Remote").Info(message);
    }

    private static void Warn(string message)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Remote").Warn(message);
    }

    // The application has its own lifetime: the server neither listens to the console nor to the signals of
    // the process, and writes nothing when it starts.
    private sealed class QuietLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
