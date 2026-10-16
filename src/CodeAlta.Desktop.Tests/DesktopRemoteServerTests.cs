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
/// the transport handshake of <c>@neoastra/client</c> done by the server, the page served with the transport
/// module, and the page's own view of the server.
/// </summary>
[TestClass]
public sealed class DesktopRemoteServerTests
{
    private const string Origin = "app://codealta";

    private static DesktopLaunchOptions? Parse(params string[] args)
        => DesktopCommandLine.TryParse(args, _ => false, _ => false, out var options, out _) ? options : null;

    [TestMethod]
    public void CommandLine_TakesTheAddressAndThePortOfTheRemoteServer()
    {
        Assert.IsNull(Parse()!.RemotePort);
        Assert.IsNull(Parse()!.RemoteHost);
        Assert.AreEqual(4200, Parse("--remote-port", "4200")!.RemotePort);
        Assert.AreEqual(0, Parse("--remote-port", "0")!.RemotePort);
        Assert.AreEqual("localhost", Parse("--remote-host", "localhost")!.RemoteHost);
        var developer = Parse("--dev", "--remote-host", "::1", "--remote-port", "4201", "--mcp-port", "4100")!;
        Assert.IsTrue(developer.Developer);
        Assert.AreEqual(("::1", 4201, 4100), (developer.RemoteHost, developer.RemotePort, developer.McpPort));
        Assert.IsNull(developer.McpHost);
        foreach (var args in new string[][]
        {
            ["--remote-port"], ["--remote-host"], ["--remote-port", "4200", "--remote-port", "4201"], ["--remote-host", "localhost", "--remote-host", "127.0.0.1"],
            ["--remote-port", "65536"], ["--remote-port", "x"], ["--remote-host", "example.com"], ["--exit", "--remote-port", "4200"],
        })
        {
            Assert.IsNull(Parse(args), string.Join(' ', args));
        }

        CollectionAssert.AreEqual(new[] { "--dev", "--mcp-port", "4100", "--remote-host", "::1", "--remote-port", "4201" }, DesktopTerminalStart.Arguments(developer).ToArray());
        using var output = new StringWriter();
        Assert.AreEqual(0, DesktopCommandLine.Run(["--help"], output, TextWriter.Null, _ => throw new AssertFailedException("Native startup was entered.")));
        foreach (var expected in new[] { "--remote-port", "--remote-host", "http://127.0.0.1:2584/", "2585" }) StringAssert.Contains(output.ToString(), expected);
    }

    [TestMethod]
    public void Endpoint_IsTheLoopbackAddress_OnThePortOfTheInstance_AndOffUnlessAsked()
    {
        Assert.AreEqual(new DesktopRemoteEndpoint("127.0.0.1", 2584, false), DesktopRemoteEndpoint.For(Parse()!));
        Assert.AreEqual(new DesktopRemoteEndpoint("127.0.0.1", 2585, false), DesktopRemoteEndpoint.For(Parse("--dev")!));
        Assert.AreEqual(new DesktopRemoteEndpoint("localhost", 4200, true), DesktopRemoteEndpoint.For(Parse("--remote-host", "localhost", "--remote-port", "4200")!));
        Assert.IsTrue(DesktopRemoteEndpoint.For(Parse("--remote-port", "2584")!).Chosen);
        var root = Path.Combine(Path.GetTempPath(), "codealta-remote-" + Guid.NewGuid().ToString("N"));
        Assert.AreEqual(new DesktopRemoteEndpoint("127.0.0.1", 0, false), DesktopRemoteEndpoint.For(Parse("--data-root", root)!));
        Assert.AreEqual("http://127.0.0.1:2584/", new DesktopRemoteEndpoint("127.0.0.1", 2584, false).Url);
        Assert.AreEqual("http://[::1]:4201/", new DesktopRemoteEndpoint("::1", 4201, true).Url);
        Assert.IsTrue(new DesktopRemoteEndpoint("localhost", 1, false).IsLoopback);
        Assert.IsFalse(new DesktopRemoteEndpoint("0.0.0.0", 1, false).IsLoopback);
    }

    /// <summary>
    /// The host's session API alone, with nothing of the window: a session opened with a send callback answers
    /// an invoke of <c>boot.status</c> through it.
    /// </summary>
    [TestMethod]
    public async Task RpcHost_AnswersAnInvoke_ThroughASessionOpenedWithACallback()
    {
        await using var rpc = BuildHost();
        var answered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = new NeoRpcSessionIdentity(DesktopRemoteServer.ViewLabel, "doc-1")
        {
            ContractHash = NeoRpcGeneratedContract.Hash, SourceOrigin = new Uri("http://127.0.0.1:1/"), IsMainFrame = true,
            ProtocolMinor = 0, Features = ["invoke", "cancel", "events"],
        };
        await using var session = rpc.OpenSession(identity, (json, _) => { answered.TrySetResult(json); return ValueTask.CompletedTask; });
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
        await using var server = new DesktopRemoteServer(new DesktopRemoteEndpoint("127.0.0.1", 0, false), () => rpc, new Page(), Origin, enabled: true);
        var changes = new List<DesktopRemoteStatus>();
        server.Changed += () => { lock (changes) changes.Add(server.Status); };
        await server.StartAsync();
        var status = server.Status;
        Assert.AreEqual((DesktopRemoteStatus.Running, true, 0), (status.State, status.Enabled, status.Clients));
        var url = new Uri(status.Url!);

        using var http = new HttpClient();
        var page = await http.GetStringAsync(url);
        StringAssert.Contains(page, "<script type=\"module\" src=\"./lib/remote.js\"></script><script type=\"module\" src=\"./assets/app.js\">");
        Assert.AreEqual("body", await http.GetStringAsync(new Uri(url, "/assets/app.js")));
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, (await http.GetAsync(new Uri(url, "/missing.js"))).StatusCode);
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, (await http.GetAsync(new Uri(url, "/favicon.ico"))).StatusCode);
        Assert.AreEqual(System.Net.HttpStatusCode.UpgradeRequired, (await http.GetAsync(new Uri(url, DesktopRemoteServer.RpcPath))).StatusCode);
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

        Assert.AreEqual(1, server.Status.Clients);
        await SendAsync(socket, """{"neoastra":1,"kind":"close"}""");
        // The server ends the connection after the page's close.
        var closed = await ReceiveRawAsync(socket);
        Assert.IsTrue(closed.MessageType == WebSocketMessageType.Close || socket.State != WebSocketState.Open, socket.State.ToString());
        await WaitUntilAsync(() => server.Status.Clients == 0);
        lock (changes) Assert.IsTrue(changes.Any(static change => change.Clients == 1), "the status counted the window");
    }

    /// <summary>A hello of another protocol is refused with a close the client reads as a mismatch.</summary>
    [TestMethod]
    public async Task Server_RefusesAnotherProtocol()
    {
        await using var rpc = BuildHost();
        await using var server = new DesktopRemoteServer(new DesktopRemoteEndpoint("127.0.0.1", 0, false), () => rpc, new Page(), Origin, enabled: true);
        await server.StartAsync();
        var url = new Uri(server.Status.Url!);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri("ws://" + url.Host + ":" + url.Port + DesktopRemoteServer.RpcPath), CancellationToken.None);
        await SendAsync(socket, """{"neoastra":1,"kind":"hello","protocol":{"major":2,"minor":0},"features":[]}""");
        using var close = JsonDocument.Parse(await ReceiveAsync(socket));
        Assert.AreEqual("close", close.RootElement.GetProperty("kind").GetString());
        Assert.AreEqual("protocol_mismatch", close.RootElement.GetProperty("code").GetString());
    }

    /// <summary>Off, the server listens to nothing; turned on it listens, and a port that is taken is replaced unless the user named it.</summary>
    [TestMethod]
    public async Task Server_IsOffUntilTurnedOn_AndTakesAFreePort_WhenItsOwnIsTaken_ButNotWhenTheUserNamedIt()
    {
        await using var rpc = BuildHost();
        await using var first = new DesktopRemoteServer(new DesktopRemoteEndpoint("127.0.0.1", 0, false), () => rpc, new Page(), Origin, enabled: false);
        await first.StartAsync();
        Assert.AreEqual((DesktopRemoteStatus.Stopped, false, (string?)null), (first.Status.State, first.Status.Enabled, first.Status.Url));
        await first.SetEnabledAsync(true);
        Assert.AreEqual(DesktopRemoteStatus.Running, first.Status.State);
        var port = new Uri(first.Status.Url!).Port;

        await using var second = new DesktopRemoteServer(new DesktopRemoteEndpoint("127.0.0.1", port, false), () => rpc, new Page(), Origin, enabled: true);
        await second.StartAsync();
        Assert.AreEqual(DesktopRemoteStatus.Running, second.Status.State);
        Assert.AreNotEqual(port, new Uri(second.Status.Url!).Port, "a port of the application's own choice that is taken is replaced");

        await using var third = new DesktopRemoteServer(new DesktopRemoteEndpoint("127.0.0.1", port, true), () => rpc, new Page(), Origin, enabled: true);
        await third.StartAsync();
        Assert.AreEqual((DesktopRemoteStatus.Failed, true), (third.Status.State, third.Status.Enabled));
        Assert.IsFalse(string.IsNullOrEmpty(third.Status.Error));

        await first.SetEnabledAsync(false);
        Assert.AreEqual((DesktopRemoteStatus.Stopped, false), (first.Status.State, first.Status.Enabled));
        using var http = new HttpClient();
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => http.GetStringAsync("http://127.0.0.1:" + port + "/"));
    }

    /// <summary>The page's view: the status with the epoch, the switch, and a launch without a server.</summary>
    [TestMethod]
    public async Task Page_SeesTheServerAndTurnsItOnAndOff()
    {
        await using var rpc = BuildHost();
        await using var server = new DesktopRemoteServer(new DesktopRemoteEndpoint("127.0.0.1", 0, false), () => rpc, new Page(), Origin, enabled: false);
        var service = new RemoteHostService(server, "epoch-1");
        Assert.AreEqual("stale_epoch", service.Status(new RemoteHostRequest("epoch-0")).Status);
        var off = service.Status(new RemoteHostRequest("epoch-1"));
        Assert.AreEqual(("ok", DesktopRemoteStatus.Stopped, false), (off.Status, off.State, off.Enabled));
        var on = await service.SetEnabledAsync(new RemoteHostEnableRequest("epoch-1", true), CancellationToken.None);
        Assert.AreEqual((DesktopRemoteStatus.Running, true, 0), (on.State, on.Enabled, on.Clients));
        StringAssert.StartsWith(on.Url, "http://127.0.0.1:");
        var back = await service.SetEnabledAsync(new RemoteHostEnableRequest("epoch-1", false), CancellationToken.None);
        Assert.AreEqual((DesktopRemoteStatus.Stopped, false, (string?)null), (back.State, back.Enabled, back.Url));
        Assert.AreEqual("unavailable", new RemoteHostService().Status(new RemoteHostRequest("epoch-1")).Status);
    }

    /// <summary>The choices are kept in the preferences: off and on the loopback address unless the user chose otherwise.</summary>
    [TestMethod]
    public void Preferences_KeepRemoteAccessOff_AndTheAddress_UntilTheUserChooses()
    {
        var temp = Path.Combine(Path.GetTempPath(), "codealta-remote-prefs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assert.IsFalse(DesktopPreferences.Load(temp).RemoteAccess);
            Assert.IsNull(DesktopPreferences.Load(temp).RemoteHost);
            Assert.IsTrue((DesktopPreferences.Default with { RemoteAccess = true, RemoteHost = "100.64.0.5" }).Save(temp));
            var loaded = DesktopPreferences.Load(temp);
            Assert.AreEqual((true, "100.64.0.5"), (loaded.RemoteAccess, loaded.RemoteHost));
            Assert.AreEqual("""{"onClose":"ask","remoteAccess":true,"remoteHost":"100.64.0.5"}""", File.ReadAllText(Path.Combine(temp, "preferences.json")));
            // Only the choices are written; an address that is none is not read.
            Assert.IsTrue(DesktopPreferences.Default.Save(temp));
            Assert.AreEqual("""{"onClose":"ask"}""", File.ReadAllText(Path.Combine(temp, "preferences.json")));
            File.WriteAllText(Path.Combine(temp, "preferences.json"), """{"onClose":"ask","remoteAccess":"yes","remoteHost":"example.com"}""");
            Assert.AreEqual((false, (string?)null), (DesktopPreferences.Load(temp).RemoteAccess, DesktopPreferences.Load(temp).RemoteHost));
            // The endpoint of a start takes the kept address when the options name none.
            Assert.AreEqual("100.64.0.5", DesktopRemoteEndpoint.For(Parse()!, "100.64.0.5").Host);
            Assert.AreEqual("::1", DesktopRemoteEndpoint.For(Parse("--remote-host", "::1")!, "100.64.0.5").Host);
            Assert.AreEqual("127.0.0.1", DesktopRemoteEndpoint.For(Parse()!, "example.com").Host);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    /// <summary>The address can be changed while the server runs: it listens again there, and the choice is kept.</summary>
    [TestMethod]
    public async Task Server_ListensOnAnotherAddress_WhenAsked_AndRemembersIt()
    {
        await using var rpc = BuildHost();
        var enabled = new List<bool>();
        var hosts = new List<string>();
        await using var server = new DesktopRemoteServer(new DesktopRemoteEndpoint("127.0.0.1", 0, false), () => rpc, new Page(), Origin, enabled: true, enabled.Add, hosts.Add);
        await server.StartAsync();
        Assert.AreEqual("127.0.0.1", server.Status.Bind);
        var before = new Uri(server.Status.Url!);
        var service = new RemoteHostService(server, "epoch-1", static () => [new("127.0.0.1", ""), new("localhost", "Loop")]);
        var moved = await service.SetBindAsync(new RemoteHostBindRequest("epoch-1", "localhost"), CancellationToken.None);
        Assert.AreEqual(("ok", DesktopRemoteStatus.Running, "localhost"), (moved.Status, moved.State, moved.Bind));
        StringAssert.StartsWith(moved.Url, "http://localhost:");
        Assert.AreNotEqual(before.Host, new Uri(moved.Url!).Host); // A free port is taken again: the instance's port is fixed, this test's is not.
        CollectionAssert.AreEqual(new[] { "localhost" }, hosts);
        Assert.AreEqual(2, moved.Addresses.Length);
        Assert.AreEqual("invalid_request", (await service.SetBindAsync(new RemoteHostBindRequest("epoch-1", "example.com"), CancellationToken.None)).Status);
        await service.SetEnabledAsync(new RemoteHostEnableRequest("epoch-1", false), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { false }, enabled);
        // Off, the address still changes for the next start, without listening.
        var off = await service.SetBindAsync(new RemoteHostBindRequest("epoch-1", "127.0.0.1"), CancellationToken.None);
        Assert.AreEqual((DesktopRemoteStatus.Stopped, "127.0.0.1", (string?)null), (off.State, off.Bind, off.Url));
        CollectionAssert.AreEqual(new[] { "localhost", "127.0.0.1" }, hosts);
    }

    [TestMethod]
    public void Addresses_StartWithThisComputer_AndEndWithEveryAddress()
    {
        var addresses = RemoteHostService.Addresses();
        Assert.AreEqual(new RemoteHostAddress("127.0.0.1", ""), addresses[0]);
        Assert.AreEqual(new RemoteHostAddress("0.0.0.0", ""), addresses[^1]);
        foreach (var address in addresses) Assert.IsTrue(DesktopCommandLine.IsMcpHost(address.Host), address.Host);
    }

    [TestMethod]
    public void TransportModule_GoesBeforeThePagesOwnScript()
    {
        const string tag = "<script type=\"module\" src=\"./lib/remote.js\"></script>";
        Assert.AreEqual("<head></head><body>" + tag + "<script type=\"module\" src=\"./assets/a.js\"></script></body>",
            DesktopRemoteServer.WithTransport("<head></head><body><script type=\"module\" src=\"./assets/a.js\"></script></body>"));
        Assert.AreEqual("<head>" + tag + "</head><body></body>", DesktopRemoteServer.WithTransport("<head></head><body></body>"));
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.IsTrue(condition());
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
