using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CodeAlta.Desktop.Browser.WebKit;

/// <summary>
/// Real input for a WKWebView: mouse and key events of AppKit, sent to the view itself, so that its page gets trusted
/// events, as from the user. Points are CSS pixels of the viewport of the page. Every call is made on the main thread.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacInput
{
    private const nuint LeftMouseDown = 1, LeftMouseUp = 2, MouseMoved = 5, LeftMouseDragged = 6, KeyDown = 10, KeyUp = 11;

    /// <summary>The flags of AppKit for the modifiers, by the bit CDP gives them (Alt 1, Control 2, Meta 4, Shift 8).</summary>
    internal static nuint Flags(int modifiers)
        => ((modifiers & 1) != 0 ? 1u << 19 : 0) | ((modifiers & 2) != 0 ? 1u << 18 : 0) | ((modifiers & 4) != 0 ? 1u << 20 : 0) | ((modifiers & 8) != 0 ? 1u << 17 : 0);

    /// <summary>Moves the pointer over the page.</summary>
    internal static bool Move(nint view, double x, double y, bool dragging) => Mouse(view, dragging ? LeftMouseDragged : MouseMoved, x, y, 0, dragging ? 1f : 0f, dragging ? "mouseDragged:" : "mouseMoved:");

    /// <summary>Presses or releases the main button at a point of the page.</summary>
    internal static bool Button(nint view, double x, double y, bool down, int clicks)
        => Mouse(view, down ? LeftMouseDown : LeftMouseUp, x, y, clicks, down ? 1f : 0f, down ? "mouseDown:" : "mouseUp:");

    /// <summary>Presses or releases a key: <paramref name="code"/> is the DOM code (<c>KeyA</c>, <c>Enter</c>), <paramref name="text"/> what it types.</summary>
    /// <returns>Whether the event was sent; false for a key AppKit has no code for.</returns>
    internal static bool Key(nint view, string code, string? text, bool down, int modifiers)
    {
        if (!KeyCodes.TryGetValue(code, out var keyCode)) return false;
        var characters = FunctionCharacters.TryGetValue(code, out var function) ? function : text ?? string.Empty;
        return SendKey(view, down, keyCode, characters, Flags(modifiers));
    }

    /// <summary>Types text as key events, one character after the other, into what has the focus in the page.</summary>
    internal static bool Type(nint view, string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var character = rune.ToString();
            // A character of a key of the keyboard goes with that key; any other is typed as text alone.
            var code = character.Length == 1 && char.IsAsciiLetter(character[0]) ? "Key" + char.ToUpperInvariant(character[0])
                : character.Length == 1 && char.IsAsciiDigit(character[0]) ? "Digit" + character : null;
            var keyCode = code is not null && KeyCodes.TryGetValue(code, out var known) ? known : (ushort)0;
            var flags = character.Length == 1 && char.IsAsciiLetterUpper(character[0]) ? 1u << 17 : 0;
            if (character == "\n" || character == "\r") (keyCode, character) = (36, "\r");
            else if (character == "\t") keyCode = 48;
            if (!SendKey(view, true, keyCode, character, flags) || !SendKey(view, false, keyCode, character, flags)) return false;
        }

        return true;
    }

    private static bool Mouse(nint view, nuint type, double x, double y, int clicks, float pressure, string action)
    {
        var window = Send(view, Selector("window"));
        if (window == 0) return false;
        var zoom = GetDouble(view, Selector("pageZoom")) * GetDouble(view, Selector("magnification"));
        if (!(zoom > 0)) zoom = 1;
        // A WKWebView is flipped: the CSS point is a point of the view, which the window takes in its own coordinates.
        var location = ConvertPoint(view, Selector("convertPoint:toView:"), new CGPoint(x * zoom, y * zoom), 0);
        var e = MouseEvent(Class("NSEvent"), Selector("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
            type, location, 0, Uptime(), GetNInt(window, Selector("windowNumber")), 0, 0, Math.Max(clicks, type is MouseMoved or LeftMouseDragged ? 0 : 1), pressure);
        if (e == 0) return false;
        Send(view, Selector(action), e);
        return true;
    }

    private static bool SendKey(nint view, bool down, ushort keyCode, string characters, nuint flags)
    {
        var window = Send(view, Selector("window"));
        if (window == 0) return false;
        var text = Text(characters);
        var e = KeyEvent(Class("NSEvent"), Selector("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:"),
            down ? KeyDown : KeyUp, default, flags, Uptime(), GetNInt(window, Selector("windowNumber")), 0, text, text, 0, keyCode);
        if (e == 0) return false;
        Send(view, Selector(down ? "keyDown:" : "keyUp:"), e);
        return true;
    }

    private static double Uptime() => GetDouble(Send(Class("NSProcessInfo"), Selector("processInfo")), Selector("systemUptime"));

    // The virtual key codes of a US keyboard, by DOM code (HIToolbox Events.h).
    private static readonly FrozenDictionary<string, ushort> KeyCodes = new Dictionary<string, ushort>(StringComparer.Ordinal)
    {
        ["KeyA"] = 0, ["KeyS"] = 1, ["KeyD"] = 2, ["KeyF"] = 3, ["KeyH"] = 4, ["KeyG"] = 5, ["KeyZ"] = 6, ["KeyX"] = 7, ["KeyC"] = 8, ["KeyV"] = 9,
        ["KeyB"] = 11, ["KeyQ"] = 12, ["KeyW"] = 13, ["KeyE"] = 14, ["KeyR"] = 15, ["KeyY"] = 16, ["KeyT"] = 17, ["Digit1"] = 18, ["Digit2"] = 19,
        ["Digit3"] = 20, ["Digit4"] = 21, ["Digit6"] = 22, ["Digit5"] = 23, ["Equal"] = 24, ["Digit9"] = 25, ["Digit7"] = 26, ["Minus"] = 27,
        ["Digit8"] = 28, ["Digit0"] = 29, ["BracketRight"] = 30, ["KeyO"] = 31, ["KeyU"] = 32, ["BracketLeft"] = 33, ["KeyI"] = 34, ["KeyP"] = 35,
        ["Enter"] = 36, ["KeyL"] = 37, ["KeyJ"] = 38, ["Quote"] = 39, ["KeyK"] = 40, ["Semicolon"] = 41, ["Backslash"] = 42, ["Comma"] = 43,
        ["Slash"] = 44, ["KeyN"] = 45, ["KeyM"] = 46, ["Period"] = 47, ["Tab"] = 48, ["Space"] = 49, ["Backquote"] = 50, ["Backspace"] = 51,
        ["Escape"] = 53, ["F5"] = 96, ["F6"] = 97, ["F7"] = 98, ["F3"] = 99, ["F8"] = 100, ["F9"] = 101, ["F11"] = 103, ["F10"] = 109, ["F12"] = 111,
        ["Insert"] = 114, ["Home"] = 115, ["PageUp"] = 116, ["Delete"] = 117, ["F4"] = 118, ["End"] = 119, ["F2"] = 120, ["PageDown"] = 121,
        ["F1"] = 122, ["ArrowLeft"] = 123, ["ArrowRight"] = 124, ["ArrowDown"] = 125, ["ArrowUp"] = 126, ["NumpadEnter"] = 76,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // What AppKit puts in the characters of the keys that type nothing (NSEvent.h function keys).
    private static readonly FrozenDictionary<string, string> FunctionCharacters = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Enter"] = "\r", ["NumpadEnter"] = "\u0003", ["Tab"] = "\t", ["Backspace"] = "\u007f", ["Escape"] = "\u001b", ["Space"] = " ",
        ["ArrowUp"] = "", ["ArrowDown"] = "", ["ArrowLeft"] = "", ["ArrowRight"] = "",
        ["F1"] = "", ["F2"] = "", ["F3"] = "", ["F4"] = "", ["F5"] = "", ["F6"] = "", ["F7"] = "",
        ["F8"] = "", ["F9"] = "", ["F10"] = "", ["F11"] = "", ["F12"] = "",
        ["Insert"] = "", ["Delete"] = "", ["Home"] = "", ["End"] = "", ["PageUp"] = "", ["PageDown"] = "",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint(double x, double y)
    {
        public double X = x, Y = y;
    }

    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";

    private static nint Class(string name) => objc_getClass(name);

    private static nint Selector(string name) => sel_registerName(name);

    private static nint Text(string text) => SendText(Class("NSString"), Selector("stringWithUTF8String:"), text);

    [DllImport(ObjectiveC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjectiveC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint a);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern double GetDouble(nint receiver, nint selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint GetNInt(nint receiver, nint selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern CGPoint ConvertPoint(nint receiver, nint selector, CGPoint point, nint view);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint SendText(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint MouseEvent(nint type, nint selector, nuint kind, CGPoint location, nuint flags, double time, nint window, nint context, nint number, nint clicks, float pressure);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint KeyEvent(nint type, nint selector, nuint kind, CGPoint location, nuint flags, double time, nint window, nint context, nint characters, nint ignoring, byte repeat, ushort code);
}
