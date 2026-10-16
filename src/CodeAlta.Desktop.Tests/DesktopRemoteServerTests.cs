using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CodeAlta.Desktop.Remote;
using CodeAlta.Desktop.Rpc;
using NeoAstra;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The remote server of the prototype: a document session of the window's RPC host over a WebSocket, with
/// the transport handshake of <c>@neoastra/client</c> done by the server, and the page served with the
/// transport script.
/// </summary>
[TestClass]
public sealed class DesktopRemoteServerTests
{
    private static DesktopLaunchOptions? Parse(params string[] args)
        => DesktopCommandLine.TryParse(args, _ => false, _ => false, out var options, out _) ? options : null;

    [TestMethod]
    public void CommandLine_TakesThePortOfTheRemoteServer()
    {
        Assert.IsNull(Parse()!.RemotePort);
        Assert.AreEqual(4200, Parse("--remote-port", "4200")!.RemotePort);
        Assert.AreEqual(0, Parse("--remote-port", "0")!.RemotePort);
        var developer = Parse("--dev", "--mcp-host", "::1", "--remote-port", "4201")!;
        Assert.IsTrue(developer.Developer);
        Assert.AreEqual(("::1", 4201), (developer.McpHost, developer.RemotePort));
        Assert.IsNull(developer.McpPort);
        foreach (var args in new string[][] { ["--remote-port"], ["--remote-port", "4200", "--remote-port", "4201"], ["--remote-port", "65536"], ["--remote-port", "x"], ["--exit", "--remote-port", "4200"] })
        {
            Assert.IsNull(Parse(args), string.Join(' ', args));
        }

        CollectionAssert.AreEqual(new[] { "--dev", "--mcp-host", "::1", "--remote-port", "4201" }, DesktopTerminalStart.Arguments(developer).ToArray());
    }

    /// <summary>
    /// The host's session API alone, with nothing of the window: a session opened with a send callback answers
    /// an invoke of <c>boot.status</c> through it.
    /// </summary>
    [TestMethod]
    public async Task RpcHost_AnswersAnInvoke_ThroughASessionOpenedWithACallback()
    {
        await using var rpc = BuildHost();
        var sent = new List<string>();
        var answered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = new NeoRpcSessionIdentity(DesktopRemoteServer.ViewLabel, "doc-1")
        {
            ContractHash = NeoRpcGeneratedContract.Hash, SourceOrigin = new Uri("http://127.0.0.1:1/"), IsMainFrame = true,
            ProtocolMinor = 0, Features = ["invoke", "cancel", "events"],
        };
        await using var session = rpc.OpenSession(identity, (json, _) => { lock (sent) sent.Add(json); answered.TrySetResult(json); return ValueTask.CompletedTask; });
        await session.ReceiveAsync(Invoke("1", "boot.status"), CancellationToken.None);
        var result = await answered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        Assert.AreEqual("result", root.GetProperty("kind").GetString());
        Assert.AreEqual("1", root.GetProperty("id").GetString());
        Assert.IsTrue(root.GetProperty("ok").GetBoolean(), result);
        Assert.AreEqual("in-development", root.GetProperty("value").GetProperty("state").GetString());
    }

    /// <summary>
    /// The server end to end: a WebSocket client does the handshake of <c>@neoastra/client</c>, gets the
    /// <c>hello_ack</c> the client parses, and an invoke through the socket is answered.
    /// </summary>
    [TestMethod]
    public async Task Server_ServesThePageAndAnswersAnInvokeOverTheSocket()
    {
        await using var rpc = BuildHost();
        await using var server = new DesktopRemoteServer("127.0.0.1", 0, () => rpc, new Page(), "app://codealta");
        await server.StartAsync();
        Assert.IsNotNull(server.Url);
        var url = new Uri(server.Url);

        using var http = new HttpClient();
        var page = await http.GetStringAsync(url);
        StringAssert.Contains(page, "<script src=\"" + DesktopRemoteServer.TransportPath + "\"></script>");
        StringAssert.Contains(page, "<script type=\"module\"");
        var script = await http.GetStringAsync(new Uri(url, DesktopRemoteServer.TransportPath));
        StringAssert.Contains(script, "Symbol.for(\"@neoastra/client/transport/v1\")");
        Assert.AreEqual("body", await http.GetStringAsync(new Uri(url, "/assets/app.js")));
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, (await http.GetAsync(new Uri(url, "/missing.js"))).StatusCode);
        // A page of another site must not reach the host through the browser.
        using var foreign = new HttpRequestMessage(HttpMethod.Get, url);
        foreign.Headers.Add("Origin", "https://example.com");
        Assert.AreEqual(System.Net.HttpStatusCode.Forbidden, (await http.SendAsync(foreign)).StatusCode);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri("ws://" + url.Host + ":" + url.Port + DesktopRemoteServer.RpcPath), CancellationToken.None);
        await SendAsync(socket, """{"neoastra":1,"kind":"hello","protocol":{"major":1,"minor":0},"features":["invoke","cancel","events"],"client":{"name":"@neoastra/client","version":"0.1.0"}}""");
        using (var ack = JsonDocument.Parse(await ReceiveAsync(socket)))
        {
            var root = ack.RootElement;
            Assert.AreEqual("hello_ack", root.GetProperty("kind").GetString());
            Assert.AreEqual(1, root.GetProperty("protocol").GetProperty("major").GetInt32());
            var runtime = root.GetProperty("runtime");
            Assert.AreEqual(DesktopRemoteServer.ViewLabel, runtime.GetProperty("viewLabel").GetString());
            Assert.IsFalse(string.IsNullOrEmpty(runtime.GetProperty("documentSessionId").GetString()));
            var pair = (runtime.GetProperty("platform").GetString(), runtime.GetProperty("backend").GetString());
            Assert.IsTrue(pair is ("windows", "webview2") or ("macos", "wkwebview") or ("linux", "webkitgtk"), pair.ToString());
            Assert.AreEqual(DesktopRemoteServer.MaximumFrameBytes, root.GetProperty("limits").GetProperty("maximumFrameBytes").GetInt32());
        }

        await SendAsync(socket, Invoke("7", "boot.status"));
        using (var result = JsonDocument.Parse(await ReceiveAsync(socket)))
        {
            var root = result.RootElement;
            Assert.AreEqual("result", root.GetProperty("kind").GetString());
            Assert.AreEqual("7", root.GetProperty("id").GetString());
            Assert.AreEqual("in-development", root.GetProperty("value").GetProperty("state").GetString());
        }

        await SendAsync(socket, """{"neoastra":1,"kind":"close"}""");
        // The server ends the connection after the page's close.
        var closed = await ReceiveRawAsync(socket);
        Assert.IsTrue(closed.MessageType == WebSocketMessageType.Close || socket.State != WebSocketState.Open, socket.State.ToString());
    }

    /// <summary>A hello of another protocol is refused with a close the client reads as a mismatch.</summary>
    [TestMethod]
    public async Task Server_RefusesAnotherProtocol()
    {
        await using var rpc = BuildHost();
        await using var server = new DesktopRemoteServer("127.0.0.1", 0, () => rpc, new Page(), "app://codealta");
        await server.StartAsync();
        var url = new Uri(server.Url!);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri("ws://" + url.Host + ":" + url.Port + DesktopRemoteServer.RpcPath), CancellationToken.None);
        await SendAsync(socket, """{"neoastra":1,"kind":"hello","protocol":{"major":2,"minor":0},"features":[]}""");
        using var close = JsonDocument.Parse(await ReceiveAsync(socket));
        Assert.AreEqual("close", close.RootElement.GetProperty("kind").GetString());
        Assert.AreEqual("protocol_mismatch", close.RootElement.GetProperty("code").GetString());
    }

    private static NeoRpcHost BuildHost()
    {
        var builder = new NeoRpcBuilder(new NeoRpcOptions { ContractHash = NeoRpcGeneratedContract.Hash, Release = true, MaximumFrameBytes = DesktopRemoteServer.MaximumFrameBytes });
        builder.AddBootService(new BootService());
        return builder.Build();
    }

    private static string Invoke(string id, string command)
        => $$"""{"neoastra":1,"kind":"invoke","id":"{{id}}","command":"{{command}}","args":{},"contract":"{{NeoRpcGeneratedContract.Hash}}"}""";

    private static Task SendAsync(WebSocket socket, string json)
        => socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string> ReceiveAsync(WebSocket socket)
    {
        var result = await ReceiveRawAsync(socket);
        Assert.AreEqual(WebSocketMessageType.Text, result.MessageType);
        return result.Text;
    }

    private static async Task<(WebSocketMessageType MessageType, string Text)> ReceiveRawAsync(WebSocket socket)
    {
        var buffer = new byte[1024 * 1024];
        var message = new MemoryStream();
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, patience.Token);
            if (result.MessageType == WebSocketMessageType.Close) return (result.MessageType, "");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return (result.MessageType, Encoding.UTF8.GetString(message.ToArray()));
        }
    }

    /// <summary>The files of a page: an entry document with a head and a module script, and one asset.</summary>
    private sealed class Page : INeoResourceProvider
    {
        public NeoResourceResponse? GetResponse(NeoResourceRequest request) => request.Uri.AbsolutePath switch
        {
            "/index.html" => NeoResourceResponse.FromBytes(Encoding.UTF8.GetBytes("<!doctype html><html><head><title>x</title></head><body><script type=\"module\" src=\"./assets/app.js\"></script></body></html>"), "text/html"),
            "/assets/app.js" => NeoResourceResponse.FromBytes(Encoding.UTF8.GetBytes("body"), "text/javascript"),
            _ => null,
        };
    }
}
