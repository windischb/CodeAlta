// Throwaway spike for .alta/plans/2026-10-13-embedded-browser-annotations.md (phase 0), Windows only.
// It answers, with NeoAstra as released: can a second view be placed, moved and hidden over the view of a window,
// on an environment of its own, with an automation of its own, and what do hidden views cost.
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using NeoAstra;
using View = NeoAstra.NeoAstra;

namespace CodeAlta.Browser.Spike;

[ComVisible(true), Guid("00F206A7-9D17-4605-91F6-4E8E4DE192E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITrySuspendCompletedHandler
{
    [PreserveSig] int Invoke(int errorCode, int isSuccessful);
}

[ComVisible(true)]
public sealed class TrySuspendHandler(Action<int, int> done) : ITrySuspendCompletedHandler
{
    public int Invoke(int errorCode, int isSuccessful)
    {
        done(errorCode, isSuccessful);
        return 0;
    }
}

internal static partial class Program
{
    private static string _root = "";
    private static StreamWriter? _log;
    private static int _exit = 1;
    private static bool _quick;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || !Path.IsPathFullyQualified(args[0]) || Directory.Exists(args[0]))
        {
            Console.Error.WriteLine("usage: alta-browser-spike <new absolute directory>");
            return 2;
        }

        _quick = args.Length == 2 && args[1] == "--quick";
        _root = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(_root);
        using var log = new StreamWriter(new FileStream(Path.Combine(_root, "spike.log"), FileMode.CreateNew)) { AutoFlush = true };
        _log = log;
        try
        {
            var result = NeoApplication.Run(new NeoApplicationOptions { ApplicationName = "CodeAlta Browser Spike", ShutdownMode = NeoApplicationShutdownMode.Explicit }, RunAsync);
            Log($"EXIT native={result} spike={_exit}");
            return result == 0 ? _exit : result;
        }
        catch (Exception exception)
        {
            Log($"FAIL {exception}");
            return 1;
        }
    }

    private static void Log(string message)
    {
        Console.WriteLine(message);
        _log?.WriteLine(message);
    }

    private const string MainPage = """
        <!doctype html><title>Spike main</title>
        <body style="margin:0;background:#7a1f1f;color:white;font:24px sans-serif">
        <h1>MAIN PAGE</h1><button id="b" onclick="window.n=(window.n||0)+1">Main button</button>
        <script>console.log('console of main')</script></body>
        """;

    private static string BrowserPage(int n) => $$"""
        <!doctype html><title>Spike browser {{n}}</title>
        <body style="margin:0;background:#1f4f7a;color:white;font:24px sans-serif">
        <h1>BROWSER PAGE {{n}}</h1><button id="b" onclick="window.n=(window.n||0)+1;document.title='clicked '+window.n">Increment</button>
        <script>window.marker='marker-{{n}}';console.log('console of browser {{n}}')</script></body>
        """;

    private static async ValueTask RunAsync(NeoApplication application)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        try
        {
            Log($"HOST {RuntimeInformation.OSDescription}; {RuntimeInformation.RuntimeIdentifier}; .NET {Environment.Version}");
            await using var window = application.CreateWindow(new NeoWindowOptions { Label = "main", Title = "CodeAlta Browser Spike", Width = 1200, Height = 800, IsVisible = false });
            application.MainWindow = window;
            var hwnd = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;

            // 0.4: two automations with disjoint filters, created before any view exists.
            await using var mainAutomation = new NeoAutomation(application, new NeoAutomationOptions { PageFilter = static view => view.ViewLabel == "main" });
            await using var browserAutomation = new NeoAutomation(application, new NeoAutomationOptions { PageFilter = static view => view.ViewLabel?.StartsWith("browser:", StringComparison.Ordinal) == true });

            // 0.3: two environments, each with a user-data root of its own, in one process.
            await using var mainEnvironment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions { UserDataRoot = Path.Combine(_root, "webview-main") }, token);
            NeoEnvironment browserEnvironment;
            try
            {
                browserEnvironment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions { UserDataRoot = Path.Combine(_root, "webview-browser") }, token);
                Log("PASS 0.3 second environment created in the same process");
            }
            catch (Exception exception)
            {
                Log($"FAIL 0.3 second environment: {exception.GetType().Name}: {exception.Message}");
                throw;
            }

            await using var browserEnvironmentLifetime = browserEnvironment;
            Log($"ENGINE {mainEnvironment.RuntimeInfo}");
            window.Show();
            await using var main = await mainEnvironment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions { ViewLabel = "main" }, token);
            await LoadAsync(main, MainPage, "MAIN PAGE", token);

            // 0.1: a second view on the same window, with a rectangle of its own.
            var first = new NeoRect(600, 100, 500, 400);
            await using var browser = await browserEnvironment.CreateWebViewAsync(NeoAstraHost.FromWin32Hwnd(hwnd), new NeoAstraOptions { ViewLabel = "browser:1", FillParent = false, Bounds = first }, token);
            await LoadAsync(browser, BrowserPage(1), "BROWSER PAGE 1", token);
            var controller = browser.GetNativeHandle(NeoNativeHandleKind.WebView2Controller).Value;
            Log($"INFO scale={window.ScaleFactor} client={window.ClientSize} createdBounds={first} controllerBounds={GetBounds(controller)} css={await Css(browser, token)}");
            Log($"INFO browserProcess main={BrowserProcessId(main)} browser={BrowserProcessId(browser)} title(main)='{DocumentTitle(main)}' title(browser)='{DocumentTitle(browser)}'");
            await Step("0.1 z-order", async () =>
            {
                await Task.Delay(400, token);
                window.Activate();
                await Task.Delay(400, token);
                LogChildren(hwnd);
                Screenshot(hwnd, "01-initial", 850, 300);
            });

            // 0.2: move, resize and hide through the borrowed controller.
            await Step("0.2 move", async () =>
            {
                var moved = new RECT { Left = 100, Top = 300, Right = 1000, Bottom = 700 };
                Log($"INFO put_Bounds hr=0x{PutBounds(controller, moved):X8} now={GetBounds(controller)}");
                await Task.Delay(300, token);
                Log($"INFO css after move={await Css(browser, token)}");
                LogChildren(hwnd);
                Screenshot(hwnd, "02-moved", 550, 500);
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < 200; i++) PutBounds(controller, new RECT { Left = 100 + i, Top = 300, Right = 1000, Bottom = 700 });
                Log($"INFO 200 x put_Bounds: {watch.Elapsed.TotalMilliseconds:F1} ms");
                PutBounds(controller, moved);
            });
            await Step("0.1 raise", async () =>
            {
                var child = FindChild(hwnd, 900, 400);
                Log($"INFO child of the browser view=0x{child:X} raise={Raise(child)}");
                await Task.Delay(300, token);
                LogChildren(hwnd);
                Screenshot(hwnd, "02b-raised", 550, 500);
                Log($"INFO hit test at (550,500): {(HitChild(hwnd, 550, 500) == child ? "browser view" : "another window")}; at (50,50): {(HitChild(hwnd, 50, 50) == child ? "browser view" : "another window")}");
                // What could bring the main view back to the top: the keyboard, a new size, a new activation.
                Log($"INFO MoveFocus(main) hr=0x{MoveFocus(main.GetNativeHandle(NeoNativeHandleKind.WebView2Controller).Value):X8}");
                await Task.Delay(300, token);
                Screenshot(hwnd, "02c-main-focused", 550, 500);
                window.Maximize();
                await Task.Delay(500, token);
                window.Restore();
                await Task.Delay(500, token);
                window.Hide();
                await Task.Delay(300, token);
                window.Show();
                window.Activate();
                await Task.Delay(500, token);
                LogChildren(hwnd);
                Screenshot(hwnd, "02d-after-resize-hide-show", 550, 500);
                await main.EvaluateScriptAsync("document.body.style.background='#7a1f20';document.body.appendChild(document.createElement('p')).textContent='repainted'", token);
                PutVisible(controller, false);
                await Task.Delay(200, token);
                PutVisible(controller, true);
                await Task.Delay(400, token);
                Screenshot(hwnd, "02e-after-hide-show-of-view", 550, 500);
                Log($"INFO MoveFocus(browser) hr=0x{MoveFocus(controller):X8}");
            });
            await Step("0.2 hide", async () =>
            {
                var watch = Stopwatch.StartNew();
                var hr = PutVisible(controller, false);
                Log($"INFO put_IsVisible(false) hr=0x{hr:X8} in {watch.Elapsed.TotalMilliseconds:F2} ms; isVisible={GetVisible(controller)}");
                await Task.Delay(300, token);
                Screenshot(hwnd, "03-hidden", 550, 500);
                Log($"INFO hidden view: visibilityState={await browser.EvaluateScriptAsync("document.visibilityState", token)} marker={await browser.EvaluateScriptAsync("window.marker", token)}");
                window.Hide();
                await Task.Delay(300, token);
                window.Show();
                await Task.Delay(300, token);
                Log($"INFO after window hide/show: isVisible={GetVisible(controller)} (true means NeoAstra showed the view again)");
                PutVisible(controller, true);
                await Task.Delay(200, token);
                Log($"INFO shown again: visibilityState={await browser.EvaluateScriptAsync("document.visibilityState", token)} bounds={GetBounds(controller)}");
                window.Maximize();
                await Task.Delay(500, token);
                Log($"INFO maximized: client={window.ClientSize} bounds={GetBounds(controller)}");
                Screenshot(hwnd, "04-maximized", 550, 500);
                window.Restore();
                await Task.Delay(500, token);
                Log($"INFO restored: client={window.ClientSize} bounds={GetBounds(controller)}");
            });

            // 0.4: each automation sees and drives its own view only.
            await Step("0.4 automations", async () =>
            {
                Log($"INFO pages main={mainAutomation.Pages.Count} browser={browserAutomation.Pages.Count}");
                var mainSnapshot = (await mainAutomation.CallToolAsync("take_snapshot", token)).Text;
                var browserSnapshot = (await browserAutomation.CallToolAsync("take_snapshot", token)).Text;
                Log($"INFO snapshot(main) hasMain={mainSnapshot.Contains("MAIN PAGE")} hasBrowser={mainSnapshot.Contains("BROWSER PAGE")}");
                Log($"INFO snapshot(browser) hasMain={browserSnapshot.Contains("MAIN PAGE")} hasBrowser={browserSnapshot.Contains("BROWSER PAGE")}");
                var uid = Regex.Match(browserSnapshot, "uid=(\\S+) button \"Increment\"").Groups[1].Value;
                var click = await browserAutomation.CallToolAsync("click", $$"""{"uid":"{{uid}}"}""", token);
                Log($"INFO click uid={uid} error={click.IsError} n(browser)={await browser.EvaluateScriptAsync("window.n", token)} n(main)={await main.EvaluateScriptAsync("window.n", token)}");
                var mainConsole = (await mainAutomation.CallToolAsync("list_console_messages", token)).Text;
                var browserConsole = (await browserAutomation.CallToolAsync("list_console_messages", token)).Text;
                Log($"INFO console(main) own={mainConsole.Contains("console of main")} other={mainConsole.Contains("console of browser")}; console(browser) own={browserConsole.Contains("console of browser 1")} other={browserConsole.Contains("console of main")}");
                await browser.EvaluateScriptAsync("setTimeout(function(){window.answered=confirm('spike?')},0);1", token);
                await Task.Delay(500, token);
                var dialog = await browserAutomation.CallToolAsync("handle_dialog", """{"action":"accept"}""", token);
                await Task.Delay(200, token);
                Log($"INFO dialog error={dialog.IsError} text='{dialog.Text.ReplaceLineEndings(" ")}' answered={await browser.EvaluateScriptAsync("window.answered", token)}");
                var hiddenClick = PutVisible(controller, false);
                var snapshotHidden = await browserAutomation.CallToolAsync("take_snapshot", token);
                var clickHidden = await browserAutomation.CallToolAsync("click", $$"""{"uid":"{{uid}}"}""", token);
                Log($"INFO hidden view: snapshot error={snapshotHidden.IsError}, click error={clickHidden.IsError}, n={await browser.EvaluateScriptAsync("window.n", token)}");
                PutVisible(controller, true);
            });

            // 0.5: captures, for the still image and for a session that is not shown.
            await Step("0.5 capture", async () =>
            {
                for (var i = 0; i < 3; i++) Log($"INFO capture visible: {await Capture(browser, token)}");
                PutVisible(controller, false);
                await Task.Delay(200, token);
                Log($"INFO capture hidden (IsVisible=false): {await Capture(browser, token)}");
                PutVisible(controller, true);
                var size = window.ClientSize;
                PutBounds(controller, new RECT { Left = size.Width + 200, Top = 0, Right = size.Width + 1100, Bottom = 400 });
                await Task.Delay(300, token);
                Log($"INFO capture placed outside the window: {await Capture(browser, token)}");
                PutBounds(controller, new RECT { Left = 100, Top = 300, Right = 1000, Bottom = 700 });
            });

            // 0.5: what hidden views cost, and what suspending them gives back.
            if (!_quick) await Step("0.5 memory", async () =>
            {
                await Task.Delay(2000, token);
                Log($"MEMORY one view: {await Memory("webview-browser")}; main environment: {await Memory("webview-main")}");
                var views = new List<View>();
                var watch = Stopwatch.StartNew();
                for (var i = 2; i <= 11; i++)
                {
                    var view = await browserEnvironment.CreateWebViewAsync(NeoAstraHost.FromWin32Hwnd(hwnd), new NeoAstraOptions { ViewLabel = $"browser:{i}", FillParent = false, Bounds = new NeoRect(100, 300, 900, 400) }, token);
                    PutVisible(view.GetNativeHandle(NeoNativeHandleKind.WebView2Controller).Value, false);
                    await LoadAsync(view, BrowserPage(i), $"BROWSER PAGE {i}", token);
                    views.Add(view);
                }

                Log($"INFO 10 hidden views created and loaded in {watch.Elapsed.TotalMilliseconds:F0} ms");
                await Task.Delay(3000, token);
                Log($"MEMORY +10 hidden views, small page: {await Memory("webview-browser")}");

                string[] sites = ["https://example.com/", "https://react.dev/", "https://github.com/NeoAstra/NeoAstra", "https://developer.mozilla.org/en-US/docs/Web/API", "https://vite.dev/"];
                for (var i = 0; i < views.Count; i++)
                {
                    try { await views[i].NavigateAsync(new Uri(sites[i % sites.Length]), token); }
                    catch (Exception exception) { Log($"INFO navigate {sites[i % sites.Length]}: {exception.GetType().Name}: {exception.Message}"); }
                }

                await Task.Delay(12000, token);
                for (var i = 0; i < views.Count; i++) Log($"INFO view {i + 2}: {views[i].Source} '{views[i].Title}' loading={views[i].IsLoading}");
                Log($"MEMORY +10 hidden views, real sites: {await Memory("webview-browser")}");

                // Suspend: ICoreWebView2_3, reached from the borrowed ICoreWebView2.
                var suspended = 0;
                var handlers = new List<object>();
                foreach (var view in views)
                {
                    var core3 = QueryCore3(view);
                    if (core3 == 0) { Log("INFO ICoreWebView2_3 not available: suspend skipped"); break; }
                    var before = IsSuspended(core3);
                    await view.EvaluateScriptAsync("window.marker2='kept'", token);
                    var completed = new TaskCompletionSource<(int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var handler = new TrySuspendHandler((error, ok) => completed.TrySetResult((error, ok)));
                    handlers.Add(handler);
                    var pointer = Marshal.GetComInterfaceForObject(handler, typeof(ITrySuspendCompletedHandler));
                    var hr = TrySuspend(core3, pointer);
                    Marshal.Release(pointer);
                    var answer = hr >= 0 ? await Task.WhenAny(completed.Task, Task.Delay(5000, token)) == completed.Task ? completed.Task.Result.ToString() : "no answer in 5 s" : "not called";
                    if (IsSuspended(core3) == 1) suspended++;
                    if (ReferenceEquals(view, views[0])) Log($"INFO TrySuspend first view: isSuspended before={before} hr=0x{hr:X8} handler={answer} after={IsSuspended(core3)}");
                    Release(core3);
                }

                Log($"INFO suspended {suspended} of {views.Count}");
                await Task.Delay(5000, token);
                Log($"MEMORY 10 views suspended: {await Memory("webview-browser")}");
                if (suspended > 0)
                {
                    var view = views[1];
                    var core3 = QueryCore3(view);
                    watch.Restart();
                    PutVisible(view.GetNativeHandle(NeoNativeHandleKind.WebView2Controller).Value, true);
                    var marker = await view.EvaluateScriptAsync("window.marker2", token);
                    Log($"INFO shown after suspend: isSuspended={IsSuspended(core3)} marker2={marker} in {watch.Elapsed.TotalMilliseconds:F0} ms, url={view.Source}");
                    await Task.Delay(300, token);
                    LogChildren(hwnd);
                    Screenshot(hwnd, "05-resumed", 550, 500);
                    Release(core3);
                }

                foreach (var view in views) await view.DisposeAsync();
                await Task.Delay(3000, token);
                Log($"MEMORY 10 views disposed: {await Memory("webview-browser")}");
            });

            _exit = 0;
        }
        catch (Exception exception)
        {
            Log($"FAIL {exception}");
        }
        finally
        {
            application.ForceShutdown();
        }
    }

    private static async Task Step(string name, Func<Task> body)
    {
        Log($"STEP {name}");
        try { await body(); }
        catch (Exception exception) { Log($"FAIL {name}: {exception.GetType().Name}: {exception.Message}"); }
    }

    private static async Task LoadAsync(View view, string html, string expected, CancellationToken token)
    {
        await view.LoadHtmlAsync(html, null, token);
        for (var i = 0; i < 100; i++)
        {
            if (await view.EvaluateScriptAsync("document.body ? document.body.innerText : ''", token) is { } text && text.Contains(expected)) return;
            await Task.Delay(50, token);
        }

        throw new InvalidOperationException($"The page with '{expected}' did not load.");
    }

    private static async Task<string> Css(View view, CancellationToken token)
        => await view.EvaluateScriptAsync("innerWidth+'x'+innerHeight+'@'+devicePixelRatio", token) ?? "?";

    private static async Task<string> Capture(View view, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(5));
        var watch = Stopwatch.StartNew();
        try
        {
            var image = await view.CaptureAsync(null, limit.Token);
            return $"{image.Width}x{image.Height}, {image.Data.Length} bytes, {watch.Elapsed.TotalMilliseconds:F0} ms";
        }
        catch (Exception exception)
        {
            return $"failed after {watch.Elapsed.TotalMilliseconds:F0} ms: {exception.GetType().Name}: {exception.Message}";
        }
    }

    // The processes of an environment, found by its user-data folder on their command lines.
    private static async Task<string> Memory(string marker)
    {
        var script = Path.Combine(_root, "processes.ps1");
        if (!File.Exists(script))
        {
            File.WriteAllText(script, "param($marker) Get-CimInstance Win32_Process -Filter \"Name='msedgewebview2.exe'\" | Where-Object { $_.CommandLine -like \"*$marker*\" } | ForEach-Object { $t = if ($_.CommandLine -match '--type=([a-z-]+)') { $Matches[1] } else { 'browser' }; \"$($_.ProcessId) $t\" }");
        }

        var lines = await Task.Run(() =>
        {
            using var process = Process.Start(new ProcessStartInfo("pwsh", ["-NoProfile", "-File", script, Path.Combine(_root, marker)]) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        });
        long privateBytes = 0, workingSet = 0;
        var kinds = new SortedDictionary<string, (int Count, long Private)>();
        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            try
            {
                using var process = Process.GetProcessById(int.Parse(parts[0]));
                privateBytes += process.PrivateMemorySize64;
                workingSet += process.WorkingSet64;
                var kind = kinds.GetValueOrDefault(parts[1]);
                kinds[parts[1]] = (kind.Count + 1, kind.Private + process.PrivateMemorySize64);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException) { }
        }

        return $"{lines.Length} processes, private {privateBytes / 1048576} MB, working set {workingSet / 1048576} MB [{string.Join(", ", kinds.Select(static kind => $"{kind.Key} x{kind.Value.Count} {kind.Value.Private / 1048576} MB"))}]";
    }

}

internal static unsafe partial class Program
{
    // ---- WebView2 through the COM tables of the objects NeoAstra lends ---------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Right - Left}x{Bottom - Top}";
    }

    private static nint* Table(nint instance) => *(nint**)instance;

    // ICoreWebView2Controller: IUnknown, get_IsVisible (3), put_IsVisible (4), get_Bounds (5), put_Bounds (6), ..., MoveFocus (12).
    private static int GetVisible(nint controller)
    {
        int value;
        return ((delegate* unmanaged[Stdcall]<nint, int*, int>)Table(controller)[3])(controller, &value) >= 0 ? value : -1;
    }

    private static int PutVisible(nint controller, bool visible) => ((delegate* unmanaged[Stdcall]<nint, int, int>)Table(controller)[4])(controller, visible ? 1 : 0);

    private static RECT GetBounds(nint controller)
    {
        RECT value;
        _ = ((delegate* unmanaged[Stdcall]<nint, RECT*, int>)Table(controller)[5])(controller, &value);
        return value;
    }

    private static int PutBounds(nint controller, RECT bounds) => ((delegate* unmanaged[Stdcall]<nint, RECT, int>)Table(controller)[6])(controller, bounds);

    // ICoreWebView2: get_BrowserProcessId (37), get_DocumentTitle (48). Both also check the numbering used for ICoreWebView2_3.
    private static uint BrowserProcessId(View view)
    {
        var core = view.GetNativeHandle(NeoNativeHandleKind.WebView2Core).Value;
        uint value;
        return ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Table(core)[37])(core, &value) >= 0 ? value : 0;
    }

    private static string DocumentTitle(View view)
    {
        var core = view.GetNativeHandle(NeoNativeHandleKind.WebView2Core).Value;
        nint value;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Table(core)[48])(core, &value) < 0) return "?";
        var title = Marshal.PtrToStringUni(value) ?? "";
        Marshal.FreeCoTaskMem(value);
        return title;
    }

    // ICoreWebView2_3: TrySuspend (68), Resume (69), get_IsSuspended (70).
    private static nint QueryCore3(View view)
    {
        var core = view.GetNativeHandle(NeoNativeHandleKind.WebView2Core).Value;
        var iid = new Guid("A0D6DF20-3B92-416D-AA0C-437A9C727857");
        nint result;
        return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Table(core)[0])(core, &iid, &result) >= 0 ? result : 0;
    }

    private static int IsSuspended(nint core3)
    {
        int value;
        return ((delegate* unmanaged[Stdcall]<nint, int*, int>)Table(core3)[70])(core3, &value) >= 0 ? value : -1;
    }

    private static int TrySuspend(nint core3, nint handler) => ((delegate* unmanaged[Stdcall]<nint, nint, int>)Table(core3)[68])(core3, handler);

    private static void Release(nint instance) => ((delegate* unmanaged[Stdcall]<nint, uint>)Table(instance)[2])(instance);

    private static int MoveFocus(nint controller) => ((delegate* unmanaged[Stdcall]<nint, int, int>)Table(controller)[12])(controller, 0);

    private static nint FindChild(nint hwnd, int width, int height)
    {
        for (var child = GetWindow(hwnd, 5); child != 0; child = GetWindow(child, 2))
        {
            GetWindowRect(child, out var rect);
            if (rect.Right - rect.Left == width && rect.Bottom - rect.Top == height) return child;
        }

        return 0;
    }

    private static bool Raise(nint child) => child != 0 && SetWindowPos(child, 0 /* HWND_TOP */, 0, 0, 0, 0, 0x0013 /* SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE */);

    private static nint HitChild(nint hwnd, int x, int y) => ChildWindowFromPointEx(hwnd, new POINT { X = x, Y = y }, 1 /* CWP_SKIPINVISIBLE */);

    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint ChildWindowFromPointEx(nint hwnd, POINT point, uint flags);

    // ---- The window as Windows has it: its children from the top one down, and a picture of it --------------------

    private static void LogChildren(nint hwnd)
    {
        GetWindowRect(hwnd, out var parent);
        var index = 0;
        for (var child = GetWindow(hwnd, 5 /* GW_CHILD: the top one */); child != 0; child = GetWindow(child, 2 /* GW_HWNDNEXT: the one below */))
        {
            var name = stackalloc char[128];
            var length = GetClassNameW(child, name, 128);
            GetWindowRect(child, out var rect);
            Log($"INFO child {index++} (top first): class={new string(name, 0, length)} visible={IsWindowVisible(child)} at ({rect.Left - parent.Left},{rect.Top - parent.Top}) {rect.Right - rect.Left}x{rect.Bottom - rect.Top} relative to the window frame");
        }
    }

    // Two pictures of the client area: what the window prints of itself, and what the screen shows there.
    // The color at a point says which page is on top: the main one is red, a browser page blue.
    private static void Screenshot(nint hwnd, string name, int probeX, int probeY)
    {
        GetClientRect(hwnd, out var client);
        int width = client.Right, height = client.Bottom;
        if (width <= 0 || height <= 0) { Log($"INFO screenshot {name}: the window has no client area"); return; }
        var origin = new POINT();
        ClientToScreen(hwnd, &origin);
        foreach (var screen in new[] { false, true })
        {
            var source = screen ? GetDC(0) : GetDC(hwnd);
            var memoryDc = CreateCompatibleDC(source);
            var bitmap = CreateCompatibleBitmap(source, width, height);
            var previous = SelectObject(memoryDc, bitmap);
            var drawn = screen ? BitBlt(memoryDc, 0, 0, width, height, source, origin.X, origin.Y, 0x00CC0020 /* SRCCOPY */) : PrintWindow(hwnd, memoryDc, 3 /* PW_CLIENTONLY | PW_RENDERFULLCONTENT */);
            SelectObject(memoryDc, previous);
            var header = new BITMAPINFOHEADER { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            var pixels = new byte[width * height * 4];
            fixed (byte* data = pixels) _ = GetDIBits(memoryDc, bitmap, 0, (uint)height, data, &header, 0);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            _ = ReleaseDC(screen ? 0 : hwnd, source);
            var file = name + (screen ? "-screen.png" : "-print.png");
            File.WriteAllBytes(Path.Combine(_root, file), Png(pixels, width, height));
            var at = (Math.Min(probeY, height - 1) * width + Math.Min(probeX, width - 1)) * 4;
            var (b, g, r) = (pixels[at], pixels[at + 1], pixels[at + 2]);
            Log($"INFO {file} {width}x{height} drawn={drawn} pixel({probeX},{probeY})=#{r:x2}{g:x2}{b:x2} {(b > r + 30 ? "BROWSER on top" : r > b + 30 ? "MAIN on top" : "other")}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, POINT* point);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    private static byte[] Png(byte[] bgra, int width, int height)
    {
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 3];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var source = (y * width + x) * 4;
                    row[1 + x * 3] = bgra[source + 2];
                    row[2 + x * 3] = bgra[source + 1];
                    row[3 + x * 3] = bgra[source];
                }

                zlib.Write(row);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 2; // RGB
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);
        var name = Encoding.ASCII.GetBytes(type);
        stream.Write(name);
        stream.Write(data);
        var crc = 0xffffffffu;
        foreach (var value in name.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        stream.Write(number);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, char* name, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte* bits, BITMAPINFOHEADER* info, uint usage);
}
