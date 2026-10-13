using System.Globalization;
using System.Text.Json;

namespace CodeAlta.Desktop.Browser;

/// <summary>An element the user picked in a page, before it is an annotation: it waits for the text of the user.</summary>
/// <param name="PageId">The page it was picked in.</param>
/// <param name="Address">The address of that page, as the host knows it.</param>
/// <param name="Title">The title of that page.</param>
/// <param name="Element">The element as the page reported it.</param>
/// <param name="Image">A picture of the element with some margin, or null when none could be taken.</param>
internal sealed record BrowserPendingPick(string PageId, string Address, string Title, BrowserElementRecord Element, BrowserCapture? Image);

/// <summary>An annotation, and whether its element is on the page the session shows.</summary>
/// <param name="Annotation">The annotation.</param>
/// <param name="Found">
/// Whether the selector of the annotation finds an element of the page in front; null when the annotation is of
/// another address, is resolved, or the page was not asked yet.
/// </param>
internal sealed record BrowserAnnotationState(BrowserAnnotation Annotation, bool? Found);

/// <summary>The annotations of a session.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Annotations">Its annotations: the open ones first, each group by number.</param>
/// <param name="PickingPageId">The page in which the user is picking an element, or null.</param>
/// <param name="Pending">The element that was picked and waits for its text, or null.</param>
internal sealed record BrowserAnnotationsInfo(string SessionId, IReadOnlyList<BrowserAnnotationState> Annotations, string? PickingPageId, BrowserPendingPick? Pending)
{
    /// <summary>Gets how many annotations wait to be done.</summary>
    internal int Open => Annotations.Count(static state => state.Annotation.IsOpen);
}

/// <summary>The outcome of a request about the annotations of a session.</summary>
/// <param name="Status">
/// <c>ok</c>; <c>unsupported</c>, <c>invalid</c>, <c>closed</c> as for the browser; <c>not_found</c> when the session
/// has no such page or annotation; <c>no_pick</c> when no element waits for a text; <c>limit</c> when the session
/// has too many annotations; <c>busy</c> when the page waits in a dialog; <c>failed</c> when the page did not answer.
/// </param>
/// <param name="Annotations">The annotations of the session after the request.</param>
/// <param name="Annotation">The annotation the request was about.</param>
internal sealed record BrowserAnnotationResult(string Status, BrowserAnnotationsInfo? Annotations = null, BrowserAnnotation? Annotation = null);

/// <summary>What changed in the browsers: the pages of a session, or its annotations.</summary>
/// <param name="Session">The browser of a session, when its pages changed.</param>
/// <param name="Annotations">The annotations of a session, when they changed.</param>
internal sealed record BrowserChange(BrowserSessionInfo? Session, BrowserAnnotationsInfo? Annotations);

// The annotations of the sessions: elements of pages the user pointed at, each with what should change. They are
// state of the same single writer as the pages, on the thread of the window. A page tells of the element with the
// picker script (annotation-picker.js); the text of an annotation never comes from a page.
internal sealed partial class DesktopBrowsers
{
    /// <summary>How often a page in pick mode is asked whether an element was picked.</summary>
    internal static readonly TimeSpan PickInterval = TimeSpan.FromMilliseconds(300);

    // The marks of the page in front are drawn again every this many rounds: a page changes without telling.
    private const int MarkRounds = 4;
    // How many rounds a page that is being loaded is waited for before the element to go to is given up.
    private const int RevealRounds = 60;
    // The margin around an element in its picture, in CSS pixels.
    private const int PictureMargin = 16;
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(3);

    private readonly BrowserAnnotationStore? _annotationStore;
    private Timer? _pickClock;
    private bool _ticking;
    private long _round;

    /// <summary>
    /// Gets whether the browsers ask their pages by themselves, on a clock, while an element is being picked or
    /// marks are shown. Tests turn it off and call <see cref="TickAsync"/>.
    /// </summary>
    internal bool Clock { get; init; } = true;

    /// <summary>The annotations of a session.</summary>
    /// <param name="sessionId">The session.</param>
    internal Task<BrowserAnnotationResult> AnnotationsAsync(string sessionId) => AnnotateAsync(sessionId, static _ => Task.FromResult(("ok", (BrowserAnnotation?)null)));

    /// <summary>
    /// Turns pick mode on or off in a page: while it is on, the page outlines the element under the pointer and a
    /// click picks it instead of reaching the page. A session picks in one page at a time.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="pageId">The page.</param>
    /// <param name="on">Whether the mode is turned on.</param>
    internal Task<BrowserAnnotationResult> PickAsync(string sessionId, string pageId, bool on) => AnnotateAsync(sessionId, async session =>
    {
        if (Find(session, pageId) is not { } page) return ("not_found", null);
        foreach (var other in session.Pages)
        {
            if (!other.Picking || (on && ReferenceEquals(other, page))) continue;
            other.Picking = false;
            if (other.View is { } view && other.Dialog is null) await EvaluateAsync(view, BrowserAnnotationScript.Pick(false));
        }

        if (!on) return ("ok", null);
        if (page.Dialog is not null) return ("busy", null);
        if (await LiveAsync(page) is not { } live) return (page.Closed ? "not_found" : "failed", null);
        using var answer = BrowserAnnotationScript.Read(await EvaluateAsync(live, BrowserAnnotationScript.Pick(true)));
        if (answer is null) return ("failed", null);
        page.Picking = true;
        StartClock();
        return ("ok", null);
    });

    /// <summary>
    /// Makes an annotation of the element that was picked, with what the user wrote. The element is the one the
    /// host holds since the pick: nothing of it is taken from the request.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="text">What the user wrote.</param>
    internal Task<BrowserAnnotationResult> SaveAnnotationAsync(string sessionId, string? text)
    {
        var written = text?.Trim();
        if (string.IsNullOrEmpty(written) || written.Length > BrowserAnnotation.MaximumTextLength) return Task.FromResult(new BrowserAnnotationResult("invalid"));
        return AnnotateAsync(sessionId, session =>
        {
            if (session.Pending is not { } pending) return Task.FromResult(("no_pick", (BrowserAnnotation?)null));
            if (session.Annotations.Count >= BrowserAnnotation.MaximumPerSession) return Task.FromResult(("limit", (BrowserAnnotation?)null));
            var id = Guid.NewGuid().ToString("N");
            var picture = pending.Image is { Data.Length: > 0 and <= BrowserAnnotation.MaximumImageBytes } ? pending.Image : null;
            var annotation = new BrowserAnnotation(id, session.NextNumber++, written, BrowserAnnotation.Open, null, null, pending.Address, pending.Title, pending.Element,
                picture is null ? null : BrowserAnnotationStore.ImageName(id, picture.MediaType), picture?.MediaType, DateTimeOffset.UtcNow);
            session.Annotations.Add(annotation);
            session.Pending = null;
            // The pictures held in memory are few: the others are read from their files.
            if (session.Pictures.Count >= 32) session.Pictures.Clear();
            session.Pictures[id] = picture;
            Keep(session, annotation, picture);
            StartClock();
            return Task.FromResult(("ok", (BrowserAnnotation?)annotation));
        });
    }

    /// <summary>Forgets the element that was picked and waits for its text.</summary>
    /// <param name="sessionId">The session.</param>
    internal Task<BrowserAnnotationResult> DiscardPickAsync(string sessionId) => AnnotateAsync(sessionId, static session =>
    {
        session.Pending = null;
        return Task.FromResult(("ok", (BrowserAnnotation?)null));
    });

    /// <summary>Resolves an annotation: what it asks for was done.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="number">The number of the annotation.</param>
    /// <param name="by">Who resolves it: <c>user</c> or <c>agent</c>.</param>
    /// <param name="note">What is said about it, or null.</param>
    internal Task<BrowserAnnotationResult> ResolveAnnotationAsync(string sessionId, int number, string by, string? note)
    {
        var said = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (said is { Length: > BrowserAnnotation.MaximumNoteLength } || by is not ("user" or "agent")) return Task.FromResult(new BrowserAnnotationResult("invalid"));
        return ChangeAsync(sessionId, number, annotation => annotation with { Status = BrowserAnnotation.Resolved, ResolvedBy = by, Note = said });
    }

    /// <summary>Opens an annotation again.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="number">The number of the annotation.</param>
    internal Task<BrowserAnnotationResult> ReopenAnnotationAsync(string sessionId, int number)
        => ChangeAsync(sessionId, number, static annotation => annotation with { Status = BrowserAnnotation.Open, ResolvedBy = null, Note = null });

    /// <summary>Removes an annotation, and its picture.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="number">The number of the annotation.</param>
    internal Task<BrowserAnnotationResult> DeleteAnnotationAsync(string sessionId, int number) => ChangeAsync(sessionId, number, static _ => null);

    /// <summary>
    /// Goes to the element of an annotation: the page of the session that shows its address comes in front, or a
    /// new page loads it, and the page scrolls to the element once its document is there.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="number">The number of the annotation.</param>
    internal async Task<BrowserAnnotationResult> RevealAnnotationAsync(string sessionId, int number)
    {
        var known = false;
        string? address = null;
        var result = await AnnotateAsync(sessionId, async session =>
        {
            if (session.Annotations.FirstOrDefault(candidate => candidate.Number == number) is not { } annotation) return ("not_found", null);
            address = annotation.Address;
            var page = (session.Active is { } front && BrowserAnnotation.SamePage(front.Address, annotation.Address) ? front : null)
                ?? session.Pages.FirstOrDefault(candidate => BrowserAnnotation.SamePage(candidate.Address, annotation.Address));
            if (page is null) return ("ok", annotation);
            known = true;
            session.Active = page;
            page.Reveal = (annotation.Element.Selector, annotation.Address, RevealRounds);
            await ApplyAsync(session);
            Publish(session);
            StartClock();
            return ("ok", annotation);
        }).ConfigureAwait(false);
        if (result.Status != "ok" || known || address is null) return result;

        // No page of the session shows the address: a new one loads it, and goes to the element when it has.
        var opened = await OpenAsync(sessionId, address).ConfigureAwait(false);
        if (opened.Status != "ok" || opened.PageId is not { } pageId) return new(opened.Status, result.Annotations, result.Annotation);
        return await AnnotateAsync(sessionId, session =>
        {
            if (Find(session, pageId) is { } page && result.Annotation is { } annotation)
            {
                page.Reveal = (annotation.Element.Selector, annotation.Address, RevealRounds);
                StartClock();
            }

            return Task.FromResult(("ok", result.Annotation));
        }).ConfigureAwait(false);
    }

    /// <summary>The picture of an annotation, or of the element that waits for its text.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="number">The number of the annotation; 0 for the element that was picked.</param>
    /// <returns>The picture, or null when there is none.</returns>
    internal async Task<BrowserCapture?> AnnotationImageAsync(string sessionId, int number)
    {
        if (!_views.IsSupported || !IsIdentifier(sessionId)) return null;
        try
        {
            return await _views.RunAsync(async () =>
            {
                if (_closed) return null;
                var session = await SessionAsync(sessionId);
                if (number == 0) return session.Pending?.Image;
                if (session.Annotations.FirstOrDefault(candidate => candidate.Number == number) is not { Image: not null } annotation) return null;
                if (session.Pictures.TryGetValue(annotation.Id, out var known)) return known;
                // What is being written is written before the picture is read.
                Task saving;
                lock (_savingGate) saving = _saving;
                var store = _annotationStore;
                var data = store is null ? null : await Task.Run(async () => { await saving; return store.LoadImage(sessionId, annotation); });
                var picture = data is null ? null : new BrowserCapture(data, annotation.ImageMediaType ?? "image/png", 0, 0);
                if (session.Pictures.Count >= 32) session.Pictures.Clear();
                session.Pictures[annotation.Id] = picture;
                return picture;
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// One round of the clock: a page in pick mode is asked whether an element was picked, the marks of the page
    /// in front of each shown session are drawn again, and a page that was loading goes to the element it was
    /// opened for. It is called on the thread of the window.
    /// </summary>
    internal async Task TickAsync()
    {
        if (_closed || _ticking) return;
        _ticking = true;
        try
        {
            _round++;
            foreach (var loading in _sessions.Values.ToArray())
            {
                if (!loading.IsCompletedSuccessfully) continue;
                var session = loading.Result;
                var changed = false;
                foreach (var page in session.Pages.ToArray())
                {
                    if (page.Closed || page.View is not { } view || page.Dialog is not null) continue;
                    if (page.Picking) changed |= await PollAsync(session, page, view);
                    if (page.Reveal is { } reveal) await RevealAsync(page, view, reveal);
                }

                changed |= await MarkAsync(session);
                if (changed) PublishAnnotations(session);
            }
        }
        finally
        {
            _ticking = false;
        }
    }

    // ---- On the thread of the window --------------------------------------------------------------------------------

    private async Task<BrowserAnnotationResult> AnnotateAsync(string sessionId, Func<Session, Task<(string Status, BrowserAnnotation? Annotation)>> body)
    {
        if (!_views.IsSupported) return new("unsupported");
        if (!IsIdentifier(sessionId)) return new("invalid");
        try
        {
            return await _views.RunAsync(async () =>
            {
                if (_closed) return new BrowserAnnotationResult("closed");
                var session = await SessionAsync(sessionId);
                var (status, annotation) = await body(session);
                return new BrowserAnnotationResult(status, PublishAnnotations(session), annotation);
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return new("closed");
        }
    }

    private Task<BrowserAnnotationResult> ChangeAsync(string sessionId, int number, Func<BrowserAnnotation, BrowserAnnotation?> change) => AnnotateAsync(sessionId, session =>
    {
        var index = session.Annotations.FindIndex(candidate => candidate.Number == number);
        if (index < 0) return Task.FromResult(("not_found", (BrowserAnnotation?)null));
        var before = session.Annotations[index];
        var after = change(before);
        if (after is null)
        {
            session.Annotations.RemoveAt(index);
            session.Pictures.Remove(before.Id);
        }
        else session.Annotations[index] = after;
        // The marks of the page in front are drawn again in the next round.
        session.Marked = null;
        Keep(session, null, null);
        return Task.FromResult(("ok", (BrowserAnnotation?)(after ?? before)));
    });

    // Reads the annotations kept for a session into it. Called off the thread of the window, before the session is used.
    private void LoadAnnotations(Session session)
    {
        if (_annotationStore?.Load(session.Id) is not { Annotations: { } kept } record) return;
        foreach (var annotation in kept.Take(BrowserAnnotation.MaximumPerSession))
        {
            if (annotation is not { Element: { Selector.Length: > 0 and <= BrowserElementRecord.MaximumSelectorLength, Ancestors: not null }, Text: not null, Address: not null, Title: not null, Number: > 0 }
                || annotation.Id is not { Length: > 0 and <= 64 } id || !id.All(char.IsAsciiLetterOrDigit) || session.Annotations.Any(known => known.Number == annotation.Number)) continue;
            session.Annotations.Add(annotation with { Status = annotation.Status == BrowserAnnotation.Resolved ? BrowserAnnotation.Resolved : BrowserAnnotation.Open });
        }

        session.NextNumber = Math.Max(Math.Max(record.Next, 1), session.Annotations.Count == 0 ? 1 : session.Annotations.Max(static annotation => annotation.Number) + 1);
    }

    // Writes the annotations of a session, after the picture of the one that was just made.
    private void Keep(Session session, BrowserAnnotation? made, BrowserCapture? picture)
    {
        if (_annotationStore is not { } store) return;
        var record = new BrowserAnnotationsRecord(session.NextNumber, [.. session.Annotations]);
        var id = session.Id;
        Save(() =>
        {
            if (made is not null && picture is not null) store.SaveImage(id, made.Id, picture);
            store.Save(id, record);
        });
    }

    private BrowserAnnotationsInfo PublishAnnotations(Session session)
    {
        var front = session.Active;
        var info = new BrowserAnnotationsInfo(session.Id,
            [.. session.Annotations.OrderBy(static annotation => annotation.IsOpen ? 0 : 1).ThenBy(static annotation => annotation.Number).Select(annotation => new BrowserAnnotationState(annotation,
                annotation.IsOpen && front is not null && session.Marked is { } marked && marked.PageId == front.Id && BrowserAnnotation.SamePage(marked.Address, annotation.Address)
                    ? marked.Found.Contains(annotation.Number) : null))],
            session.Pages.FirstOrDefault(static page => page.Picking)?.Id, session.Pending);
        if (!_sessions.ContainsKey(session.Id)) return info;
        if (_publishedAnnotations.TryGetValue(session.Id, out var before) && before.PickingPageId == info.PickingPageId && ReferenceEquals(before.Pending, info.Pending)
            && before.Annotations.SequenceEqual(info.Annotations)) return before;
        _publishedAnnotations[session.Id] = info;
        Tell(new BrowserChange(null, info));
        return info;
    }

    // Asks a page in pick mode where it is. Returns whether what the session shows of its annotations changed.
    private async Task<bool> PollAsync(Session session, Page page, IBrowserView view)
    {
        using var answer = BrowserAnnotationScript.Read(await EvaluateAsync(view, BrowserAnnotationScript.Poll));
        if (page.Closed || !page.Picking || !ReferenceEquals(page.View, view)) return false;
        var state = answer is not null && answer.RootElement.TryGetProperty("state", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        switch (state)
        {
            case "picking":
                return false;
            case "picked" when answer!.RootElement.TryGetProperty("element", out var reported) && BrowserElementRecord.Read(reported, out _, out _) is { } element:
                page.Picking = false;
                // The address and the title are the ones the host knows: what the page says of itself is not taken.
                session.Pending = new BrowserPendingPick(page.Id, page.Address, page.Title, element, await PictureAsync(view, element));
                return true;
            case "off" or null:
                // Another document was loaded, which knows of no pick mode: the mode goes on in it.
                if (!page.Loading) await EvaluateAsync(view, BrowserAnnotationScript.Pick(true));
                return false;
            default:
                // Escape in the page, or a pick that says nothing of its element.
                page.Picking = false;
                return true;
        }
    }

    // A picture of an element with some margin, as far as the viewport shows it.
    private static async Task<BrowserCapture?> PictureAsync(IBrowserView view, BrowserElementRecord element)
    {
        int left = Math.Max(element.X - PictureMargin, 0), top = Math.Max(element.Y - PictureMargin, 0);
        int right = Math.Min(element.X + element.Width + PictureMargin, element.ViewportWidth), bottom = Math.Min(element.Y + element.Height + PictureMargin, element.ViewportHeight);
        if (right - left < 2 || bottom - top < 2) return null;
        using var timeout = new CancellationTokenSource(ScriptTimeout);
        try
        {
            var capture = await view.CaptureRegionAsync(new BrowserBounds(left, top, right - left, bottom - top), timeout.Token);
            return capture is { Data.Length: > 0 and <= BrowserAnnotation.MaximumImageBytes } ? capture : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // Draws the marks of the open annotations of the page in front of a shown session. Returns whether what is
    // found changed.
    private async Task<bool> MarkAsync(Session session)
    {
        if (session.Placement is null || session.Active is not { View: { } view, Dialog: null, Loading: false, Closed: false } front) return false;
        var wanted = session.Annotations.Where(annotation => annotation.IsOpen && BrowserAnnotation.SamePage(annotation.Address, front.Address)).ToArray();
        if (wanted.Length == 0 && session.Marked is null) return false;
        var key = string.Join('\n', wanted.Select(static annotation => annotation.Number.ToString(CultureInfo.InvariantCulture) + " " + annotation.Element.Selector));
        var same = session.Marked is { } before && before.PageId == front.Id && before.Address == front.Address && before.Key == key;
        if (same && _round % MarkRounds != 0) return false;
        using var answer = BrowserAnnotationScript.Read(await EvaluateAsync(view, BrowserAnnotationScript.Mark(wanted)));
        if (answer is null || !ReferenceEquals(session.Active, front) || !ReferenceEquals(front.View, view)) return false;
        var found = new HashSet<int>();
        if (answer.RootElement.TryGetProperty("found", out var numbers) && numbers.ValueKind == JsonValueKind.Array)
        {
            foreach (var number in numbers.EnumerateArray())
            {
                if (number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var value) && wanted.Any(annotation => annotation.Number == value)) found.Add(value);
            }
        }

        var changed = !same || !session.Marked!.Found.SetEquals(found);
        session.Marked = wanted.Length == 0 ? null : new Marks(front.Id, front.Address, key, found);
        return changed;
    }

    // Scrolls a page to the element it was asked to show, once its document is the one of the annotation.
    private async Task RevealAsync(Page page, IBrowserView view, (string Selector, string Address, int Rounds) reveal)
    {
        if (page.Loading || !BrowserAnnotation.SamePage(page.Address, reveal.Address))
        {
            page.Reveal = reveal.Rounds <= 1 ? null : reveal with { Rounds = reveal.Rounds - 1 };
            return;
        }

        page.Reveal = null;
        await EvaluateAsync(view, BrowserAnnotationScript.Reveal(reveal.Selector));
    }

    private static async Task<string?> EvaluateAsync(IBrowserView view, string script)
    {
        using var timeout = new CancellationTokenSource(ScriptTimeout);
        try
        {
            return await view.EvaluateAsync(script, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private void StartClock()
    {
        if (!Clock || _closed || _pickClock is not null) return;
        _pickClock = new Timer(static state => ((DesktopBrowsers)state!).PostTick(), this, PickInterval, PickInterval);
    }

    private void PostTick()
    {
        if (_closed) return;
        try
        {
            _ = _views.RunAsync(async () => { await TickAsync(); return true; }).ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (ObjectDisposedException)
        {
            // The application is ending.
        }
    }

    // The marks drawn in a page: which page and address, for which annotations, and which of them were found.
    private sealed record Marks(string PageId, string Address, string Key, HashSet<int> Found);
}
