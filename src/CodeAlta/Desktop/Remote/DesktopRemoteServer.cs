using System.Globalization;
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

/// <summary>
/// Serves the page of the application to a browser on another computer, with its RPC over a WebSocket: the
/// same <see cref="NeoRpcHost"/> the window uses, one document session per connection.
/// </summary>
/// <remarks>
/// <para>
/// The transport handshake that NeoAstra does for a WebView is done here: the client sends <c>hello</c>, the
/// server answers <c>hello_ack</c>, and every other frame is passed to the session as is. The browser gets the
/// transport object <c>@neoastra/client</c> looks for (<see cref="TransportScript"/>), injected into the entry
/// document before its module script, so the frontend runs unchanged.
/// </para>
/// <para>
/// There is no authentication yet: whoever reaches the port has the user's control of the application. The
/// server listens on the loopback address unless a start names another one, and refuses a request whose
/// <c>Host</c> or <c>Origin</c> is not this computer (<see cref="DesktopMcpGuard"/>), as the MCP server does.
/// </para>
/// </remarks>
internal sealed class DesktopRemoteServer : IAsyncDisposable
{
    /// <summary>The port of the normal instance, next to the MCP server's.</summary>
    internal const int DefaultPort = 2584;

    /// <summary>The port of the developer instance, which runs beside the normal one.</summary>
    internal const int DeveloperPort = 2585;

    /// <summary>The path of the WebSocket endpoint that carries the RPC frames.</summary>
    internal const string RpcPath = "/rpc";

    /// <summary>The path of the script that installs the transport in the page.</summary>
    internal const string TransportPath = "/_remote/transport.js";

    /// <summary>The label of the view every remote document session has; the page reads it to know it is remote.</summary>
    internal const string ViewLabel = "remote";

    /// <summary>The largest inbound frame, the same as the window's.</summary>
    internal const int MaximumFrameBytes = 128 * 1024 * 1024;

    private const int MaximumJsonDepth = 32;
    private const int MaximumConnections = 8;
    private static readonly string[] Features = ["invoke", "cancel", "events"];

    private readonly string _host;
    private readonly int _port;
    private readonly Func<NeoRpcHost?> _rpc;
    private readonly INeoResourceProvider _assets;
    private readonly string _origin;
    private int _connections;
    private WebApplication? _application;

    /// <summary>Creates the server. It does not listen until <see cref="StartAsync"/>.</summary>
    /// <param name="host"><c>localhost</c> or an IP address.</param>
    /// <param name="port">The port; 0 for a free one.</param>
    /// <param name="rpc">The RPC host of the window; null while the application has none yet.</param>
    /// <param name="assets">The files of the page, as the window loads them.</param>
    /// <param name="origin">The origin the window's page is served from (<c>app://codealta</c>), which the asset provider answers.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    internal DesktopRemoteServer(string host, int port, Func<NeoRpcHost?> rpc, INeoResourceProvider assets, string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        _host = host;
        _port = port;
        _rpc = rpc;
        _assets = assets;
        _origin = origin.TrimEnd('/');
    }

    /// <summary>The address the server listens at, once it does.</summary>
    internal string? Url { get; private set; }

    /// <summary>Starts listening.</summary>
    /// <exception cref="IOException">The port is taken.</exception>
    internal async Task StartAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ApplicationName = "CodeAlta", ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            if (string.Equals(_host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                if (_port == 0) kestrel.Listen(IPAddress.Loopback, 0);
                else kestrel.ListenLocalhost(_port);
            }
            else kestrel.Listen(IPAddress.Parse(_host), _port);
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

        _application = application;
        var port = application.Urls.Select(static url => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Port : 0).FirstOrDefault(static port => port > 0);
        var host = _host.Contains(':', StringComparison.Ordinal) ? "[" + _host + "]" : _host;
        Url = "http://" + host + ":" + port.ToString(CultureInfo.InvariantCulture) + "/";
        Info($"The remote server listens at {Url}");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var application = _application;
        _application = null;
        if (application is null) return;
        try
        {
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await application.StopAsync(patience.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException)
        {
            Warn($"The remote server did not stop cleanly: {exception.Message}");
        }

        await application.DisposeAsync().ConfigureAwait(false);
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
            finally { Interlocked.Decrement(ref _connections); }
            return;
        }

        if (context.Request.Method != HttpMethods.Get && context.Request.Method != HttpMethods.Head) { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        if (path == TransportPath)
        {
            context.Response.ContentType = "text/javascript; charset=utf-8";
            await context.Response.WriteAsync(TransportScript, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (path == "/") path = "/index.html";
        var response = _assets.GetResponse(new NeoResourceRequest(new Uri(_origin + path), context.Request.Method, new Dictionary<string, string>(), null,
            path.EndsWith(".html", StringComparison.Ordinal) ? NeoResourceKind.Document : NeoResourceKind.Other, path == "/index.html", ReadOnlyMemory<byte>.Empty));
        if (response is null || response.StatusCode != 200) { context.Response.StatusCode = response?.StatusCode ?? StatusCodes.Status404NotFound; return; }
        context.Response.ContentType = response.MimeType ?? "application/octet-stream";
        if (path == "/index.html")
        {
            // The transport must exist before the module script of the page runs: a classic script in the head does.
            var document = response.FilePath is { } file ? await File.ReadAllTextAsync(file, context.RequestAborted).ConfigureAwait(false) : Encoding.UTF8.GetString(response.Bytes.Span);
            document = document.Replace("<head>", "<head><script src=\"" + TransportPath + "\"></script>", StringComparison.Ordinal);
            await context.Response.WriteAsync(document, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (response.FilePath is { } filePath) await context.Response.SendFileAsync(filePath, context.RequestAborted).ConfigureAwait(false);
        else await context.Response.Body.WriteAsync(response.Bytes, context.RequestAborted).ConfigureAwait(false);
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
            ContractHash = NeoRpcGeneratedContract.Hash, SourceOrigin = new Uri(Url ?? "http://127.0.0.1/"), IsMainFrame = true, WholeViewTrust = false,
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
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None).ConfigureAwait(false); }
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

    /// <summary>
    /// The script the page gets before its module script: the transport object <c>@neoastra/client</c> finds at
    /// <c>Symbol.for("@neoastra/client/transport/v1")</c>, over a WebSocket to <see cref="RpcPath"/> of the page's
    /// own origin. Frames sent before the socket is open wait for it; the socket closing is told to the page as a
    /// host close, so the page reloads as it does when the window's session is closed.
    /// </summary>
    internal const string TransportScript = """
        (() => {
          const url = (location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/rpc";
          const socket = new WebSocket(url);
          const pending = [];
          const queued = [];
          let handler;
          socket.onopen = () => { for (const json of pending) socket.send(json); pending.length = 0; };
          socket.onmessage = event => {
            let value;
            try { value = JSON.parse(event.data); } catch { return; }
            if (handler) handler(value); else queued.push(value);
          };
          socket.onclose = () => { const close = { neoastra: 1, kind: "close", reason: "rpc_session_closed" }; if (handler) handler(close); else queued.push(close); };
          globalThis[Symbol.for("@neoastra/client/transport/v1")] = Object.freeze({
            metadata: Object.freeze({ handshakeTimeoutMilliseconds: 10000, maximumDiagnosticQueue: 100, maximumFrameBytes: 134217728 }),
            send(frame) {
              const json = JSON.stringify(frame);
              if (socket.readyState === WebSocket.OPEN) socket.send(json);
              else if (socket.readyState === WebSocket.CONNECTING) pending.push(json);
              else throw { code: "connection_closed", message: "The connection to CodeAlta is closed." };
            },
            setReceiveHandler(next) {
              handler = next;
              for (const value of queued.splice(0)) next(value);
              return () => { if (handler === next) handler = undefined; };
            },
          });
        })();
        """;

    private static void Info(string message)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Remote").Info(message);
    }

    private static void Warn(string message)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Remote").Warn(message);
    }

    private sealed class QuietLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
