using CodeAlta.Desktop.Browser;
using CodeAlta.Desktop.Ui;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using static CodeAlta.Desktop.Tests.DesktopUiToolsTests;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The plugin that lets a session drive the browser of its own session: how a session gets the tools, the commands
/// for its pages, and what a session whose commands are reviewed may do.
/// </summary>
[TestClass]
public sealed class DesktopBrowserPluginTests
{
    [TestMethod]
    public async Task ASession_GetsTheBrowserToolsWhenItAsksForThem_AndDrivesTheBrowserOfItsOwnSession()
    {
        using var fixture = new DesktopBrowserToolsTests.Fixture();
        var (ui, tools, sessions) = (fixture.Ui, fixture.Tools, fixture.Sessions);
        var global = Directory.CreateDirectory(Path.Combine(fixture.Root, "global")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(fixture.Root, "project")).FullName;
        var provider = new ScriptedProvider();
        var pluginAlta = new PluginAltaServiceBridge();
        await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = global, CurrentProjectPath = project, IsHeadless = true, AutoApproveOwnedPermissions = true, OwnedCommandReceiptCapacity = 32,
            PluginBuiltIns = [DesktopBrowserPlugin.Definition(tools)],
            PluginServices = new DesktopPluginServices(pluginAlta, new DesktopPluginUi()),
            ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
        }, CancellationToken.None);
        var commands = DesktopAltaTools.Attach(host, new AltaReminderService(new AltaServiceCollection()), pluginAlta);
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Looks at its application");
        var other = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Does not");

        // A session does not carry the tools until it asks: its instructions say how, and that it has a browser.
        provider.Script(Say("nothing to look at"));
        await SendAsync(host, session, "one");
        var first = provider.Requests.Single();
        Assert.IsFalse(first.Tools.Contains("browser_take_snapshot"));
        StringAssert.Contains(first.Instructions, DesktopBrowserPlugin.Guidance(active: false, reviewed: false, pages: 0));

        // It asks with an alta command: the tools join the running turn, and it opens a page and reads it in its next steps.
        provider.Requests.Clear();
        provider.Script(Call("alta", """{"args":["browser","activate"]}"""), Call("browser_new_page", """{"url":"localhost:5173"}"""),
            Call("browser_take_snapshot", """{"verbose":false}"""), Say("the page shows a list"));
        await SendAsync(host, session, "two");
        Assert.AreEqual(4, provider.Requests.Count);
        Assert.IsFalse(provider.Requests[0].Tools.Contains("browser_take_snapshot"));
        foreach (var tool in tools.Tools) CollectionAssert.Contains(provider.Requests[1].Tools, tool.Name);
        var activation = provider.Requests[1].LastToolResult!;
        StringAssert.Contains(activation, "\"type\":\"alta.browser.activate\"");
        StringAssert.Contains(activation, "\"toolsAvailable\":\"now\"");
        StringAssert.Contains(activation, "\"readingOnly\":false");
        StringAssert.Contains(activation, $"\"registeredToolCount\":{tools.Tools.Count}");
        Assert.AreEqual("Successfully navigated to http://localhost:5173/.\n## Pages\n1: Page of localhost (http://localhost:5173/) [selected]", provider.Requests[2].LastToolResult);
        Assert.AreEqual("## Latest page snapshot\nuid=1_0 RootWebArea \"Page\"", provider.Requests[3].LastToolResult);
        // The page is one of the browser of that session, and the automation was asked for it.
        var page = fixture.Browsers.Peek(session.SessionId)!.Pages.Single();
        Assert.AreEqual("http://localhost:5173/", page.Address);
        CollectionAssert.AreEqual(new[] { "navigate_page", "take_snapshot" }, ui.Calls.Select(static call => call.Name).ToArray());
        StringAssert.Contains(ui.Calls[1].Arguments, $"\"pageId\":{ui.PageOf(page.Id)}");
        Assert.AreEqual(project, ui.Calls[1].Files.BaseDirectory, "a file is saved in the folder the session works in");
        Assert.IsTrue(sessions.IsActive(session.SessionId));

        // Its next runs start with them, and its instructions say what it has. Another session has neither tools nor pages.
        provider.Requests.Clear();
        provider.Script(Say("still there"));
        await SendAsync(host, session, "three");
        CollectionAssert.Contains(provider.Requests.Single().Tools, "browser_click");
        StringAssert.Contains(provider.Requests.Single().Instructions, DesktopBrowserPlugin.Guidance(active: true, reviewed: false, pages: 1));
        provider.Requests.Clear();
        provider.Script(Say("no browser tools for me"));
        await SendAsync(host, other, "one");
        Assert.IsFalse(provider.Requests.Single().Tools.Contains("browser_click"));
        StringAssert.Contains(provider.Requests.Single().Instructions, DesktopBrowserPlugin.Guidance(active: false, reviewed: false, pages: 0));

        // It gives them back the same way; its pages stay.
        provider.Requests.Clear();
        provider.Script(Call("alta", """{"args":["browser","deactivate"]}"""), Say("done"));
        await SendAsync(host, session, "four");
        StringAssert.Contains(provider.Requests[1].LastToolResult, "\"type\":\"alta.browser.deactivate\"");
        Assert.IsFalse(sessions.IsActive(session.SessionId));
        provider.Requests.Clear();
        provider.Script(Say("gone"));
        await SendAsync(host, session, "five");
        Assert.IsFalse(provider.Requests.Single().Tools.Contains("browser_click"));
        StringAssert.Contains(provider.Requests.Single().Instructions, DesktopBrowserPlugin.Guidance(active: false, reviewed: false, pages: 1));

        // A caller that is no session has no browser to drive.
        foreach (var command in new[] { new[] { "browser", "activate" }, ["browser", "deactivate"], ["browser", "list"], ["browser", "open", "example.com"], ["browser", "close", "1"] })
        {
            var refused = await commands.InvokeAsync(command, caller: AltaCallerIdentity.Cli);
            Assert.AreEqual(AltaExitCodes.Failure, refused.ExitCode, refused.Transcript);
            StringAssert.Contains(refused.Transcript, "browser.noSession");
        }

        var caller = new AltaCallerIdentity { Kind = "agent", SourceSessionId = session.SessionId };
        var status = await commands.InvokeAsync(["browser", "status"], caller: caller);
        StringAssert.Contains(status.Transcript, "\"type\":\"alta.browser.status\"");
        StringAssert.Contains(status.Transcript, "\"active\":false");
        StringAssert.Contains(status.Transcript, "\"pageCount\":1");
        StringAssert.Contains(status.Transcript, "\"browser_take_snapshot\"");
        StringAssert.Contains((await commands.InvokeAsync(["browser", "status"], caller: AltaCallerIdentity.Cli)).Transcript, "\"pageCount\":0");
    }

    [TestMethod]
    public async Task TheCommands_ListOpenAndCloseThePagesOfTheCallingSession_WithoutTheTools()
    {
        using var fixture = new DesktopBrowserToolsTests.Fixture();
        await using var host = await CreateHostAsync(fixture, out var pluginAlta);
        var commands = DesktopAltaTools.Attach(host, new AltaReminderService(new AltaServiceCollection()), pluginAlta);
        var a = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-a" };
        var b = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-b" };

        var empty = await commands.InvokeAsync(["browser", "list"], caller: a);
        Assert.AreEqual(AltaExitCodes.Success, empty.ExitCode, empty.Transcript);
        StringAssert.Contains(empty.Transcript, "\"type\":\"alta.browser.list\"");
        StringAssert.Contains(empty.Transcript, "\"pages\":[]");

        var opened = await commands.InvokeAsync(["browser", "open", "example.com"], caller: a);
        Assert.AreEqual(AltaExitCodes.Success, opened.ExitCode, opened.Transcript);
        StringAssert.Contains(opened.Transcript, "\"type\":\"alta.browser.open\"");
        StringAssert.Contains(opened.Transcript, "\"message\":\"Successfully navigated to https://example.com/.\"");
        StringAssert.Contains(opened.Transcript, "\"pages\":[{\"number\":1,\"title\":\"Page of example.com\",\"url\":\"https://example.com/\",\"selected\":true,\"loading\":false}]");
        Assert.AreEqual(AltaExitCodes.Success, (await commands.InvokeAsync(["browser", "open", "localhost:3000"], caller: a)).ExitCode);
        Assert.IsFalse(fixture.Sessions.IsActive("session-a"), "the commands need no tools");

        // What is no page of the web is not opened, and the command says which addresses are.
        foreach (var address in new[] { "file:///C:/x.html", "app://codealta/" })
        {
            var refused = await commands.InvokeAsync(["browser", "open", address], caller: a);
            Assert.AreEqual(AltaExitCodes.Failure, refused.ExitCode, refused.Transcript);
            StringAssert.Contains(refused.Transcript, "browser.openFailed");
            StringAssert.Contains(refused.Transcript, "http and https addresses only");
        }

        Assert.AreNotEqual(AltaExitCodes.Success, (await commands.InvokeAsync(["browser", "open"], caller: a)).ExitCode, "an address is required");

        // A session lists and closes its own pages, by their numbers.
        var listed = await commands.InvokeAsync(["browser", "list"], caller: a);
        StringAssert.Contains(listed.Transcript, "{\"number\":2,\"title\":\"Page of localhost\",\"url\":\"http://localhost:3000/\",\"selected\":true,\"loading\":false}");
        StringAssert.Contains((await commands.InvokeAsync(["browser", "list"], caller: b)).Transcript, "\"pages\":[]");
        var none = await commands.InvokeAsync(["browser", "close", "1"], caller: b);
        Assert.AreEqual(AltaExitCodes.Failure, none.ExitCode, none.Transcript);
        StringAssert.Contains(none.Transcript, "browser.closeFailed");
        foreach (var number in new[] { "3", "0", "one", "-1" }) Assert.AreNotEqual(AltaExitCodes.Success, (await commands.InvokeAsync(["browser", "close", number], caller: a)).ExitCode, number);
        Assert.AreEqual(2, fixture.Browsers.Peek("session-a")!.Pages.Count);

        var closed = await commands.InvokeAsync(["browser", "close", "1"], caller: a);
        Assert.AreEqual(AltaExitCodes.Success, closed.ExitCode, closed.Transcript);
        StringAssert.Contains(closed.Transcript, "\"type\":\"alta.browser.close\"");
        StringAssert.Contains(closed.Transcript, "\"pages\":[{\"number\":1,\"title\":\"Page of localhost\"");
        Assert.AreEqual(AltaExitCodes.Success, (await commands.InvokeAsync(["browser", "close", "1"], caller: a)).ExitCode);
        Assert.AreEqual(0, fixture.Browsers.Peek("session-a")!.Pages.Count, "the last page can be closed too");
    }

    [TestMethod]
    public async Task ASessionWhoseCommandsAreReviewed_HasTheTools_AndOnlyReadsItsPages()
    {
        using var fixture = new DesktopBrowserToolsTests.Fixture();
        var (ui, sessions) = (fixture.Ui, fixture.Sessions);
        var reviewed = false;
        sessions.Reviewed = _ => Volatile.Read(ref reviewed);
        var provider = new ScriptedProvider();
        await using var host = await CreateHostAsync(fixture, out var pluginAlta, provider);
        var commands = DesktopAltaTools.Attach(host, new AltaReminderService(new AltaServiceCollection()), pluginAlta);
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Asks first");
        var caller = new AltaCallerIdentity { Kind = "agent", SourceSessionId = session.SessionId };
        Assert.AreEqual(AltaExitCodes.Success, (await commands.InvokeAsync(["browser", "open", "example.com"], caller: caller)).ExitCode);

        // The browser is not where a review is answered: the session gets the tools, and is told that it reads only.
        Volatile.Write(ref reviewed, true);
        provider.Script(Call("alta", """{"args":["browser","activate"]}"""), Call("browser_click", """{"uid":"1_2"}"""), Call("browser_take_snapshot", "{}"), Say("read it"));
        await SendAsync(host, session, "one");
        StringAssert.Contains(provider.Requests[0].Instructions, DesktopBrowserPlugin.Guidance(active: false, reviewed: true, pages: 1));
        StringAssert.Contains(provider.Requests[1].LastToolResult, "\"readingOnly\":true");
        CollectionAssert.Contains(provider.Requests[1].Tools, "browser_click");
        Assert.AreEqual(DesktopBrowserTools.ReviewedRefusal, provider.Requests[2].LastToolResult);
        Assert.AreEqual("## Latest page snapshot\nuid=1_0 RootWebArea \"Page\"", provider.Requests[3].LastToolResult);
        CollectionAssert.AreEqual(new[] { "navigate_page", "take_snapshot" }, ui.Calls.Select(static call => call.Name).ToArray());

        // Its commands follow the same rule: it lists its pages, and neither opens nor closes one.
        foreach (var command in new[] { new[] { "browser", "open", "example.org" }, ["browser", "close", "1"] })
        {
            var denied = await commands.InvokeAsync(command, caller: caller);
            Assert.AreEqual(AltaExitCodes.PolicyDenied, denied.ExitCode, denied.Transcript);
            StringAssert.Contains(denied.Transcript, "browser.denied");
        }

        Assert.AreEqual(AltaExitCodes.Success, (await commands.InvokeAsync(["browser", "list"], caller: caller)).ExitCode);
        StringAssert.Contains((await commands.InvokeAsync(["browser", "status"], caller: caller)).Transcript, "\"readingOnly\":true");
        Assert.AreEqual(1, fixture.Browsers.Peek(session.SessionId)!.Pages.Count);

        // Its next run says so in its instructions, and it acts again when its commands are not reviewed any more.
        provider.Requests.Clear();
        provider.Script(Say("still reading"));
        await SendAsync(host, session, "two");
        StringAssert.Contains(provider.Requests.Single().Instructions, DesktopBrowserPlugin.Guidance(active: true, reviewed: true, pages: 1));
        Volatile.Write(ref reviewed, false);
        provider.Requests.Clear();
        provider.Script(Call("browser_click", """{"uid":"1_2"}"""), Say("clicked"));
        await SendAsync(host, session, "three");
        StringAssert.Contains(provider.Requests[0].Instructions, DesktopBrowserPlugin.Guidance(active: true, reviewed: false, pages: 1));
        Assert.AreEqual("done", provider.Requests[1].LastToolResult);
    }

    [TestMethod]
    public async Task ThePlugin_IsTurnedOffLikeAnyOther()
    {
        using var fixture = new DesktopBrowserToolsTests.Fixture();
        foreach (var (name, configuration, active) in new[] { ("on", "", true), ("off", "[plugins.browser]\nenabled = false\n", false) })
        {
            await using var host = await CreateHostAsync(fixture, out var pluginAlta, name: name, configuration: configuration);
            var commands = DesktopAltaTools.Attach(host, new AltaReminderService(new AltaServiceCollection()), pluginAlta);

            var plugins = await commands.InvokeAsync(["plugin", "list"], caller: AltaCallerIdentity.Cli);
            Assert.AreEqual(active, plugins.Transcript.Contains("builtin:browser", StringComparison.Ordinal), plugins.Transcript);
            // Without the plugin there is no command for the browser of a session.
            var status = await commands.InvokeAsync(["browser", "status"], caller: AltaCallerIdentity.Cli);
            Assert.AreEqual(active, status.ExitCode == AltaExitCodes.Success, status.Transcript);
        }
    }

    [TestMethod]
    public void AHostThatReviewsCommands_StillListsThePlugin_AndASystemWithoutBrowserViewsDoesNot()
    {
        using var fixture = new DesktopBrowserToolsTests.Fixture();
        var window = new DesktopUiSessions();

        CollectionAssert.AreEqual(new[] { "git", "jira", "mcp", "statistics", "landing", "browser", "ui" },
            DesktopPlugins.ForWindow(fixture.Ui, window, reviewsCommands: false, browser: fixture.Tools).Select(static plugin => plugin.Id).ToArray());
        // The browser of a session is not where a review is answered: its sessions read their pages.
        var reviewing = DesktopPlugins.ForWindow(fixture.Ui, window, reviewsCommands: true, browser: fixture.Tools);
        CollectionAssert.AreEqual(new[] { "git", "jira", "mcp", "statistics", "landing", "browser" }, reviewing.Select(static plugin => plugin.Id).ToArray());
        Assert.AreEqual(typeof(DesktopBrowserPlugin), reviewing[^1].PluginType);
        Assert.IsInstanceOfType<DesktopBrowserPlugin>(reviewing[^1].Factory());

        using var none = new DesktopBrowserToolsTests.Fixture(new FakeViews { IsSupported = false });
        CollectionAssert.AreEqual(new[] { "git", "jira", "mcp", "statistics", "landing", "ui" },
            DesktopPlugins.ForWindow(fixture.Ui, window, reviewsCommands: false, browser: none.Tools).Select(static plugin => plugin.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "git", "jira", "mcp", "statistics", "landing", "ui" },
            DesktopPlugins.ForWindow(fixture.Ui, window, reviewsCommands: false).Select(static plugin => plugin.Id).ToArray());
    }

    internal static Task<CodeAltaHost> CreateHostAsync(DesktopBrowserToolsTests.Fixture fixture, out PluginAltaServiceBridge pluginAlta, ScriptedProvider? provider = null,
        string name = "global", string configuration = "")
    {
        var global = Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
        var project = Directory.CreateDirectory(Path.Combine(fixture.Root, "project")).FullName;
        if (configuration.Length != 0) File.WriteAllText(Path.Combine(global, "config.toml"), configuration);
        var bridge = pluginAlta = new PluginAltaServiceBridge();
        return CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = global, CurrentProjectPath = project, IsHeadless = true, AutoApproveOwnedPermissions = true, OwnedCommandReceiptCapacity = 32,
            PluginBuiltIns = [DesktopBrowserPlugin.Definition(fixture.Tools)],
            PluginServices = new DesktopPluginServices(bridge, new DesktopPluginUi()),
            ConfigureModelProviders = provider is null ? null : registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
        }, CancellationToken.None);
    }
}
