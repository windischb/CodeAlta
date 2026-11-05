using System.Text.Json;
using CodeAlta.Browser.Annotations;
using CodeAlta.Browser.Toolbar;
using CodeAlta.Browser.Drivers;
using CodeAlta.Browser.Tools;

namespace CodeAlta.Desktop.Browser.WebKit;

/// <summary>
/// The CodeAlta toolbar in a browser tab of the built-in browser: the same script as in Chrome, in a JavaScript world of
/// its own. WebKit has no execution context to name a document by, so the world gives each document a token of its
/// own, which every message carries and every call checks. Without DevTools, an annotated element has no style rules
/// and no stack of its creation: the source is found from its text, its selector and the address of the page.
/// </summary>
internal sealed partial class WebKitPage : IAnnotatablePage
{
    /// <summary>How far around the elements of an annotation its picture reaches, and how large it may be, in CSS pixels.</summary>
    private const double PictureMargin = 16, PictureMaximumWidth = 1200, PictureMaximumHeight = 900;

    // What the toolbar reports through: the binding the toolbar calls posts to the channel of its world, with the token
    // of its document. The page's world sees neither the binding nor the channel.
    private static readonly string ToolbarPrelude = $$"""
        (function () {
          if (!Object.prototype.hasOwnProperty.call(globalThis, '__codealtaToolbarDocument')) {
            Object.defineProperty(globalThis, '__codealtaToolbarDocument', { value: Math.random().toString(36).slice(2) + Date.now().toString(36) });
          }
          globalThis.{{ToolbarScript.BindingName}} = function (payload) {
            window.webkit.messageHandlers.{{MacContentWorld.ChannelName}}.postMessage(JSON.stringify({ __codealtaToolbar: globalThis.__codealtaToolbarDocument, payload: String(payload) }));
          };
        })();
        """;

    // The documents the toolbar reported from, by their token, as the number the plugin knows them by.
    private readonly Dictionary<string, int> _documents = new(StringComparer.Ordinal);
    private readonly Lock _documentGate = new();

    /// <inheritdoc />
    public async Task<IAsyncDisposable> StartToolbarAsync(Action<ToolbarMessage> onMessage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        void Received(string text)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("__codealtaToolbar", out var token) || token.GetString() is not { Length: > 0 and <= 64 } name
                    || !root.TryGetProperty("payload", out var payload) || payload.GetString() is not { } body) return;
                if (ToolbarMessage.TryParse(body, DocumentOf(name)) is { } message) onMessage(message);
            }
            catch (JsonException)
            {
                // Not a message of the toolbar.
            }
        }

        var world = await DriveAsync(driver =>
        {
            var opened = driver.World(ToolbarScript.WorldName, ToolbarPrelude + "\n" + ToolbarScript.Source);
            opened.Message += Received;
            return Task.FromResult(opened);
        }).ConfigureAwait(false);
        return new ToolbarRegistration(this, world, Received);
    }

    /// <inheritdoc />
    public Task RecordNodeCreationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task<bool> CallToolbarAsync(int contextId, string call, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(call);
        string? token;
        lock (_documentGate) token = _documents.FirstOrDefault(document => document.Value == contextId).Key;
        if (token is null) return false;
        // Only the document the plugin means: another one in the same tab answers false, as a gone context does in Chrome.
        var expression = $"globalThis.__codealtaToolbarDocument === {JsonSerializer.Serialize(token)} ? {ToolbarScript.Expression(call)} : false";
        try
        {
            var answer = await DriveAsync(driver => driver.World(ToolbarScript.WorldName, ToolbarPrelude + "\n" + ToolbarScript.Source).CallAsync(expression, CallTimeout, cancellationToken)).ConfigureAwait(false);
            return answer.Error is null && answer.Json == "true";
        }
        catch (BrowserToolException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<ElementEvidence> InspectAsync(int contextId, string id, int index, AnnotatedElement element, CancellationToken cancellationToken)
        => Task.FromResult(ElementEvidence.None);

    /// <inheritdoc />
    public async Task<byte[]?> PictureAsync(AnnotationArea area, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (area.Width <= 0 || area.Height <= 0) return null;
        // A rectangle the user drew is the picture; elements they chose are shown with some of what is around them.
        var margin = area.Drawn ? 0 : PictureMargin;
        var x = Math.Max(0, area.X - margin);
        var y = Math.Max(0, area.Y - margin);
        var width = Math.Min(area.Width + 2 * margin, PictureMaximumWidth);
        var height = Math.Min(area.Height + 2 * margin, PictureMaximumHeight);
        try
        {
            return await ScreenshotAsync(new ScreenshotRequest { Clip = (x, y, width, height) }, cancellationToken).ConfigureAwait(false);
        }
        catch (BrowserToolException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> ViewPictureAsync(AnnotationArea area, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (area.Width <= 0 || area.Height <= 0) return null;
        try
        {
            // The view captures what it shows, with what the toolbar draws over the page.
            return await ScreenshotAsync(new ScreenshotRequest { Clip = (area.X, area.Y, Math.Min(area.Width, PictureMaximumWidth), Math.Min(area.Height, PictureMaximumHeight)) }, cancellationToken).ConfigureAwait(false);
        }
        catch (BrowserToolException)
        {
            return null;
        }
    }

    // The number of a document of the tab, given the first time its toolbar speaks.
    private int DocumentOf(string token)
    {
        lock (_documentGate)
        {
            if (_documents.TryGetValue(token, out var number)) return number;
            // A tab keeps the numbers of its last documents only: older ones are gone.
            if (_documents.Count >= 32) _documents.Remove(_documents.MinBy(static document => document.Value).Key);
            number = _documents.Count == 0 ? 1 : _documents.Values.Max() + 1;
            _documents[token] = number;
            return number;
        }
    }

    private sealed class ToolbarRegistration(WebKitPage page, MacContentWorld world, Action<string> received) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await page._browsers.OnViewThreadAsync(() =>
                {
                    world.Message -= received;
                    return Task.FromResult(true);
                }).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The application ends, and the world with it.
            }
        }
    }
}
