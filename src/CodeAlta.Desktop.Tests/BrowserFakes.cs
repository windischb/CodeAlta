using CodeAlta.Desktop.Browser;

namespace CodeAlta.Desktop.Tests;

/// <summary>The browser views of the tests: they create views that record what is asked of them, on the thread of the caller.</summary>
internal sealed class FakeViews : IBrowserViews
{
    private readonly Lock _gate = new();
    private readonly List<FakeView> _all = [];

    public bool IsSupported { get; init; } = true;

    public bool Fail { get; set; }

    public IReadOnlyList<FakeView> All
    {
        get { lock (_gate) return [.. _all]; }
    }

    /// <summary>The view a page has now: the last one that was created for it.</summary>
    public FakeView Of(string pageId) => All.Last(view => view.PageId == pageId);

    public Task<T> RunAsync<T>(Func<Task<T>> body) => body();

    public ValueTask<IBrowserView> CreateAsync(string pageId, Action<Uri> openPage, CancellationToken cancellationToken)
    {
        if (Fail) throw new InvalidOperationException("No browser here.");
        var view = new FakeView(pageId, openPage);
        lock (_gate) _all.Add(view);
        return ValueTask.FromResult<IBrowserView>(view);
    }
}

/// <summary>A browser view of the tests.</summary>
internal sealed class FakeView(string pageId, Action<Uri> openPage) : IBrowserView
{
    public string PageId { get; } = pageId;

    public Action<Uri> OpenPage { get; } = openPage;

    public List<Uri> Navigations { get; } = [];

    public List<string> Commands { get; } = [];

    public BrowserBounds? Bounds { get; private set; }

    public bool Disposed { get; private set; }

    public string Address => Navigations.Count == 0 ? BrowserAddress.Blank : Navigations[^1].AbsoluteUri;

    public BrowserViewState State { get; private set; } = new(BrowserAddress.Blank, string.Empty, false, false, false);

    public event Action? Changed;

    public void Report(BrowserViewState state)
    {
        State = state;
        Changed?.Invoke();
    }

    public void Navigate(Uri address) => Navigations.Add(address);

    public void GoBack() => Commands.Add("back");

    public void GoForward() => Commands.Add("forward");

    public void Reload() => Commands.Add("reload");

    public void Stop() => Commands.Add("stop");

    public void OpenDevTools() => Commands.Add("devtools");

    public void Focus() => Commands.Add("focus");

    public void Place(BrowserBounds? bounds) => Bounds = bounds;

    /// <summary>What was asked for a capture, in order: "begin" and "end", each with whether the view was hidden then.</summary>
    public List<string> Captures { get; } = [];

    public void BeginCapture() => Captures.Add(Bounds is null ? "begin hidden" : "begin shown");

    public void EndCapture() => Captures.Add(Bounds is null ? "end hidden" : "end shown");

    /// <summary>The scripts the view was asked to run, in order.</summary>
    public List<string> Scripts { get; } = [];

    /// <summary>What the picker of the page answers to the script that asks where pick mode is; null for a page that does not answer.</summary>
    public string? PickState { get; set; } = """{"state":"off"}""";

    /// <summary>The numbers of the annotations whose element the page finds.</summary>
    public HashSet<int> Found { get; } = [];

    /// <summary>What the view gives as a picture of what it shows: a white JPEG of the viewport the picker reports, 1280 by 800 pixels.</summary>
    public BrowserCapture? Picture { get; set; } = Jpeg(1280, 800);

    /// <summary>The number of the badge that the page says was clicked, with where its element is, as the picker writes it; null for none.</summary>
    public string? OpenedState { get; set; }

    /// <summary>What the picker answers when it is asked to bring an element into sight.</summary>
    public string LocateState { get; set; } = """{"found":true,"scrolled":false,"rect":{"x":10,"y":20,"width":30,"height":40},"viewport":{"width":1280,"height":800}}""";

    public ValueTask<string?> EvaluateAsync(string script, CancellationToken cancellationToken)
    {
        Scripts.Add(script);
        // What the picker returns is a JSON text, which the view gives as a JSON string.
        static string? Text(string? json) => json is null ? null : System.Text.Json.JsonSerializer.Serialize(json);
        if (script.Contains(".pick(true)", StringComparison.Ordinal)) return ValueTask.FromResult(Text(PickState is null ? null : """{"state":"picking"}"""));
        if (script.Contains(".pick(false)", StringComparison.Ordinal)) return ValueTask.FromResult(Text("""{"state":"off"}"""));
        if (script.Contains(".poll()", StringComparison.Ordinal)) return ValueTask.FromResult(Text(PickState));
        if (script.Contains(".mark(", StringComparison.Ordinal))
        {
            var asked = System.Text.RegularExpressions.Regex.Matches(script, "\"number\":(\\d+)").Select(static match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            return ValueTask.FromResult(Text($$"""{"found":[{{string.Join(',', asked.Where(Found.Contains))}}]}"""));
        }

        if (script.Contains(".opened()", StringComparison.Ordinal))
        {
            var opened = OpenedState ?? """{"number":null}""";
            OpenedState = null;
            return ValueTask.FromResult(Text(opened));
        }

        if (script.Contains(", false)", StringComparison.Ordinal) && script.Contains(".reveal(", StringComparison.Ordinal)) return ValueTask.FromResult(Text(LocateState));

        return ValueTask.FromResult(Text("""{"found":true}"""));
    }

    /// <summary>The answers to the dialogs of the page, in order: "accept", "accept:&lt;text&gt;" or "dismiss".</summary>
    public List<string> Answers { get; } = [];

    /// <summary>The page opens a JavaScript dialog, as the automation of the views would tell.</summary>
    public void Ask(BrowserDialog dialog) => Report(State with { Dialog = dialog });

    /// <summary>Somebody else answered the dialog of the page: a tool of the session, or its time ran out.</summary>
    public void AnswerElsewhere() => Report(State with { Dialog = null });

    public ValueTask<bool> AnswerDialogAsync(bool accept, string? text)
    {
        if (State.Dialog is null) return ValueTask.FromResult(false);
        Answers.Add(accept ? text is null ? "accept" : "accept:" + text : "dismiss");
        Report(State with { Dialog = null });
        return ValueTask.FromResult(true);
    }

    public ValueTask<BrowserCapture?> CaptureAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Picture);

    private static BrowserCapture Jpeg(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        bitmap.Erase(SkiaSharp.SKColors.White);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80);
        return new BrowserCapture(data.ToArray(), "image/jpeg", width, height);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Bounds = null;
        return ValueTask.CompletedTask;
    }
}
