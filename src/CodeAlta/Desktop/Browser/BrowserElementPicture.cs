using System.Text.Json;
using SkiaSharp;

namespace CodeAlta.Desktop.Browser;

/// <summary>Where an element is in its page, as the page reported it: its rectangle in the viewport and the size of the viewport, in CSS pixels.</summary>
/// <param name="X">The left edge of the rectangle.</param>
/// <param name="Y">The top edge of the rectangle.</param>
/// <param name="Width">The width of the rectangle.</param>
/// <param name="Height">The height of the rectangle.</param>
/// <param name="ViewportWidth">The width of the viewport.</param>
/// <param name="ViewportHeight">The height of the viewport.</param>
internal sealed record BrowserElementPlace(int X, int Y, int Width, int Height, int ViewportWidth, int ViewportHeight)
{
    /// <summary>
    /// Reads the <c>rect</c> and the <c>viewport</c> of what the picker returned. Null when the viewport has no
    /// size: a place without one cannot be shown anywhere.
    /// </summary>
    /// <param name="owner">The object that has both.</param>
    internal static BrowserElementPlace? Read(JsonElement owner)
    {
        if (owner.ValueKind != JsonValueKind.Object) return null;
        var rect = owner.TryGetProperty("rect", out var box) ? box : default;
        var viewport = owner.TryGetProperty("viewport", out var size) ? size : default;
        var place = new BrowserElementPlace(BrowserElementRecord.Number(rect, "x"), BrowserElementRecord.Number(rect, "y"), BrowserElementRecord.Number(rect, "width"),
            BrowserElementRecord.Number(rect, "height"), BrowserElementRecord.Number(viewport, "width"), BrowserElementRecord.Number(viewport, "height"));
        return place is { ViewportWidth: > 0, ViewportHeight: > 0, Width: >= 0, Height: >= 0 } ? place : null;
    }
}

/// <summary>
/// The picture of an element, cut out of a picture of the whole viewport. A part of a view is never captured by
/// itself: on WebView2 that capture goes through the DevTools protocol with a clip, and Chromium shows the clip in
/// the view while it captures, so the page flickers and the element is seen alone for a moment.
/// </summary>
internal static class BrowserElementPicture
{
    /// <summary>
    /// The part of a picture of the viewport that shows an element with a margin around it, in pixels of the
    /// picture: the rectangle in CSS pixels is scaled by the ratio of the picture to the viewport, and kept
    /// inside the picture. Null when nothing of the element is in it.
    /// </summary>
    /// <param name="place">Where the element is.</param>
    /// <param name="margin">The margin around the element, in CSS pixels.</param>
    /// <param name="pictureWidth">The width of the picture, in pixels.</param>
    /// <param name="pictureHeight">The height of the picture, in pixels.</param>
    internal static SKRectI? Region(BrowserElementPlace place, int margin, int pictureWidth, int pictureHeight)
    {
        ArgumentNullException.ThrowIfNull(place);
        if (place.ViewportWidth <= 0 || place.ViewportHeight <= 0 || pictureWidth <= 0 || pictureHeight <= 0) return null;
        double scaleX = (double)pictureWidth / place.ViewportWidth, scaleY = (double)pictureHeight / place.ViewportHeight;
        var left = (int)Math.Clamp(Math.Floor((place.X - (double)margin) * scaleX), 0, pictureWidth);
        var top = (int)Math.Clamp(Math.Floor((place.Y - (double)margin) * scaleY), 0, pictureHeight);
        var right = (int)Math.Clamp(Math.Ceiling(((double)place.X + place.Width + margin) * scaleX), 0, pictureWidth);
        var bottom = (int)Math.Clamp(Math.Ceiling(((double)place.Y + place.Height + margin) * scaleY), 0, pictureHeight);
        return right - left < 2 || bottom - top < 2 ? null : new SKRectI(left, top, right, bottom);
    }

    /// <summary>Cuts the picture of an element out of a picture of the viewport, as a PNG.</summary>
    /// <param name="viewport">The picture of the whole viewport.</param>
    /// <param name="place">Where the element is.</param>
    /// <param name="margin">The margin around the element, in CSS pixels.</param>
    /// <returns>The picture, or null when nothing of the element is in the picture or it cannot be read.</returns>
    internal static BrowserCapture? Cut(BrowserCapture viewport, BrowserElementPlace place, int margin)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(place);
        if (viewport.Data.IsEmpty) return null;
        using var data = SKData.CreateCopy(viewport.Data.Span);
        using var bitmap = SKBitmap.Decode(data);
        if (bitmap is null || Region(place, margin, bitmap.Width, bitmap.Height) is not { } region) return null;
        using var part = new SKBitmap();
        if (!bitmap.ExtractSubset(part, region)) return null;
        using var image = SKImage.FromBitmap(part);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded is null ? null : new BrowserCapture(encoded.ToArray(), "image/png", region.Width, region.Height);
    }
}
