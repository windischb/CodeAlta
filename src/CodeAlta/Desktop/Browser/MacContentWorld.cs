using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodeAlta.Desktop.Browser;

/// <summary>What a script of a content world answered: its value as JSON, or why it failed.</summary>
/// <param name="Json">The value as JSON text; <c>null</c> for a value that was <c>undefined</c> or <c>null</c>.</param>
/// <param name="Error">Why the script failed, or null when it did not.</param>
internal sealed record ContentWorldAnswer(string? Json, string? Error);

/// <summary>
/// A JavaScript world of its own in the documents of a WKWebView, which NeoAstra does not offer on macOS: a script of
/// CodeAlta runs there beside the page, which sees neither its functions nor its channel to the host. The channel is a
/// script message handler registered in that world only: the page's world has no <c>webkit.messageHandlers</c> at all.
/// </summary>
/// <remarks>
/// <para>
/// A call runs an expression in the world and answers through the channel, with the number of the call: the host
/// needs no completion block. What a script of the world posts by itself, which is not an answer, is a message.
/// </para>
/// <para>
/// Every member is used on the main thread. The script of the world is added to each document of the main frame. Only
/// a WKWebView has worlds: the class is created on macOS only.
/// </para>
/// </remarks>
internal sealed class MacContentWorld : IDisposable
{
    /// <summary>The name of the channel of a world in its documents: <c>window.webkit.messageHandlers.codealta</c>.</summary>
    internal const string ChannelName = "codealta";

    /// <summary>The longest message that is read, in UTF-16 units.</summary>
    internal const int MaximumMessageLength = 4 * 1024 * 1024;

    private readonly nint _view;
    private readonly nint _world;
    private readonly nint _handler;
    private readonly nint _controller;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<ContentWorldAnswer>> _calls = new();
    private GCHandle _self;
    private long _sequence;
    private bool _disposed;

    /// <summary>Opens a world in the documents of a view, with a script that runs at the start of each document of its main frame.</summary>
    /// <param name="view">The WKWebView.</param>
    /// <param name="name">The name of the world.</param>
    /// <param name="script">The script of the world, or null for none.</param>
    /// <exception cref="ArgumentException"><paramref name="view"/> is zero or <paramref name="name"/> is blank.</exception>
    internal MacContentWorld(nint view, string name, string? script)
    {
        if (view == 0) throw new ArgumentException("The view is required.", nameof(view));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _view = view;
        Name = name;
        _world = Send(Send(Class("WKContentWorld"), Selector("worldWithName:"), Text(name)), Selector("retain"));
        _controller = Send(Send(view, Selector("configuration")), Selector("userContentController"));
        _handler = Send(Send(HandlerClass.Value.Class, Selector("alloc")), Selector("init"));
        _self = GCHandle.Alloc(this, GCHandleType.Weak);
        Marshal.WriteIntPtr(_handler, HandlerClass.Value.Offset, GCHandle.ToIntPtr(_self));
        Send(_controller, Selector("addScriptMessageHandler:contentWorld:name:"), _handler, _world, Text(ChannelName));
        if (script is null) return;
        // At the start of each document of the main frame; the document there now gets it at once.
        var user = SendScript(Send(Class("WKUserScript"), Selector("alloc")), Selector("initWithSource:injectionTime:forMainFrameOnly:inContentWorld:"), Text(script), 0, 1, _world);
        Send(_controller, Selector("addUserScript:"), user);
        Send(user, Selector("release"));
        Run(script);
    }

    /// <summary>Gets the name of the world.</summary>
    internal string Name { get; }

    /// <summary>Occurs on the main thread with what a script of the world posted by itself, as text.</summary>
    internal event Action<string>? Message;

    /// <summary>Runs a script in the world of the main frame, without waiting for it.</summary>
    /// <param name="script">The script.</param>
    internal void Run(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (_disposed) return;
        Send(_view, Selector("evaluateJavaScript:inFrame:inContentWorld:completionHandler:"), Text(script), 0, _world, 0);
    }

    /// <summary>
    /// Runs an expression in the world of the main frame and gives what it answers, a promise once it settles. A
    /// document that goes away meanwhile never answers: the call then ends with the timeout.
    /// </summary>
    /// <param name="expression">The expression.</param>
    /// <param name="timeout">How long the answer is waited for.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>The answer; an error <c>timeout</c> when none came.</returns>
    internal async Task<ContentWorldAnswer> CallAsync(string expression, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = Interlocked.Increment(ref _sequence);
        var answer = new TaskCompletionSource<ContentWorldAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[id] = answer;
        try
        {
            Run($$"""
                (async function () {
                  const post = value => window.webkit.messageHandlers.{{ChannelName}}.postMessage(JSON.stringify(value));
                  try {
                    const value = await (
                {{expression}}
                    );
                    let json = null;
                    try { json = value === undefined ? null : JSON.stringify(value); } catch (_) { json = JSON.stringify(String(value)); }
                    post({ __codealtaAnswer: {{id}}, json: json === undefined ? null : json });
                  } catch (error) {
                    post({ __codealtaAnswer: {{id}}, error: String(error && error.message || error) });
                  }
                })();
                """);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                return await answer.Task.WaitAsync(limit.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ContentWorldAnswer(null, "timeout");
            }
        }
        finally
        {
            _calls.TryRemove(id, out _);
        }
    }

    /// <summary>Closes the channel of the world. Its script stays in the documents the view loads, idle without a channel.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Send(_controller, Selector("removeScriptMessageHandlerForName:contentWorld:"), Text(ChannelName), _world);
        Marshal.WriteIntPtr(_handler, HandlerClass.Value.Offset, 0);
        Send(_handler, Selector("release"));
        Send(_world, Selector("release"));
        if (_self.IsAllocated) _self.Free();
        foreach (var call in _calls.Values) call.TrySetResult(new ContentWorldAnswer(null, "closed"));
    }

    private void Receive(string text)
    {
        if (_disposed || text.Length > MaximumMessageLength) return;
        if (text.Contains("\"__codealtaAnswer\"", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("__codealtaAnswer", out var number) && number.TryGetInt64(out var id))
                {
                    if (_calls.TryRemove(id, out var call))
                    {
                        call.TrySetResult(root.TryGetProperty("error", out var error)
                            ? new ContentWorldAnswer(null, error.GetString() ?? "error")
                            : new ContentWorldAnswer(root.TryGetProperty("json", out var json) && json.ValueKind == JsonValueKind.String ? json.GetString() : null, null));
                    }

                    return;
                }
            }
            catch (JsonException)
            {
                // Not an answer: a message of the world.
            }
        }

        Message?.Invoke(text);
    }

    // ---- The Objective-C class of the handlers: created once, then constant ----------------------------------------

    private sealed record HandlerType(nint Class, int Offset);

    // The class a process registers once: it never changes after.
    private static readonly Lazy<HandlerType> HandlerClass = new(CreateHandlerClass, LazyThreadSafetyMode.ExecutionAndPublication);

    // What the runtime calls when a script of a world posts: kept for the life of the process, as the class that names it.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReceivedHandler(nint self, nint command, nint controller, nint message);

    private static readonly ReceivedHandler ReceivedCallback = Received;

    private static HandlerType CreateHandlerClass()
    {
        var type = objc_allocateClassPair(Class("NSObject"), "CodeAltaContentWorldHandler", 0);
        // The handler keeps the managed world it belongs to: a weak GC handle, written at its creation.
        class_addIvar(type, "_codealtaWorld", (nuint)nint.Size, (byte)Math.Log2(nint.Size), "^v");
        class_addMethod(type, Selector("userContentController:didReceiveScriptMessage:"), Marshal.GetFunctionPointerForDelegate(ReceivedCallback), "v@:@@");
        class_addProtocol(type, objc_getProtocol("WKScriptMessageHandler"));
        objc_registerClassPair(type);
        return new HandlerType(type, (int)ivar_getOffset(class_getInstanceVariable(type, "_codealtaWorld")));
    }

    private static void Received(nint self, nint command, nint controller, nint message)
    {
        try
        {
            var pointer = Marshal.ReadIntPtr(self, HandlerClass.Value.Offset);
            if (pointer == 0 || GCHandle.FromIntPtr(pointer).Target is not MacContentWorld world) return;
            var body = Send(message, Selector("body"));
            if (body == 0 || GetByte(body, Selector("isKindOfClass:"), Class("NSString")) == 0) return;
            if (Marshal.PtrToStringUTF8(Send(body, Selector("UTF8String"))) is { } text) world.Receive(text);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A message that cannot be read is dropped: nothing may go back into native code.
        }
    }

    // ---- The runtime ------------------------------------------------------------------------------------------------

    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";

    private static nint Class(string name) => objc_getClass(name);

    private static nint Selector(string name) => sel_registerName(name);

    private static nint Text(string text) => SendText(Class("NSString"), Selector("stringWithUTF8String:"), text);

    [DllImport(ObjectiveC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjectiveC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjectiveC)] private static extern nint objc_getProtocol(string name);
    [DllImport(ObjectiveC)] private static extern nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);
    [DllImport(ObjectiveC)] private static extern void objc_registerClassPair(nint type);
    [DllImport(ObjectiveC)] private static extern byte class_addMethod(nint type, nint selector, nint implementation, string types);
    [DllImport(ObjectiveC)] private static extern byte class_addProtocol(nint type, nint protocol);
    [DllImport(ObjectiveC)] private static extern byte class_addIvar(nint type, string name, nuint size, byte alignment, string types);
    [DllImport(ObjectiveC)] private static extern nint class_getInstanceVariable(nint type, string name);
    [DllImport(ObjectiveC)] private static extern nint ivar_getOffset(nint ivar);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint a);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint a, nint b);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint a, nint b, nint c);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint a, nint b, nint c, nint d);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint SendScript(nint receiver, nint selector, nint source, nint injection, byte mainFrameOnly, nint world);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern byte GetByte(nint receiver, nint selector, nint a);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint SendText(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
}
