using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Catalog.Worktrees;
using NeoAstra;

namespace CodeAlta.Desktop.Ui;

/// <summary>
/// The tools of the window, over the browser automation of NeoAstra, which has the operations of Chrome
/// DevTools MCP: the same names, the same arguments, the same result text.
/// </summary>
/// <remarks>
/// <para>
/// The pages of the automation are the views of the application. The two tools that open and close browser
/// windows (<c>new_page</c>, <c>close_page</c>) are left out: these tools are for the window of the
/// application, not for browsing.
/// </para>
/// <para>
/// Automation is on from the creation of this object to its disposal: create it before the views load a
/// document, so that the console messages and the requests of the page are known from its first one.
/// </para>
/// </remarks>
internal sealed class DesktopUiAutomation : IDesktopUi, Browser.IBrowserDialogs, IAsyncDisposable
{
    private readonly NeoAutomation _automation;

    /// <summary>Turns automation on for the views of an application: its windows, not the browsers of the sessions.</summary>
    /// <param name="application">The application whose views the tools see and drive.</param>
    /// <param name="filesDirectory">The folder the tools read their files from; created when it does not exist.</param>
    /// <exception cref="ArgumentNullException"><paramref name="application"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="filesDirectory"/> is blank.</exception>
    internal DesktopUiAutomation(NeoApplication application, string filesDirectory)
        : this(application, filesDirectory, browsers: false)
    {
    }

    private DesktopUiAutomation(NeoApplication application, string filesDirectory, bool browsers)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentException.ThrowIfNullOrWhiteSpace(filesDirectory);
        FilesDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(filesDirectory));
        Directory.CreateDirectory(FilesDirectory);
        // The pages of the browsers of the sessions are no windows of the application: the tools of the window leave
        // them alone, and the tools of the browsers see nothing else. No view is a page of both.
        _automation = new NeoAutomation(application, new NeoAutomationOptions
        {
            AllowedDirectories = { FilesDirectory },
            PageFilter = browsers ? static view => Browser.WebViewBrowserViews.IsBrowserView(view) : static view => !Browser.WebViewBrowserViews.IsBrowserView(view),
        });
        var tools = _automation.Tools.Select(static tool => new DesktopUiTool(tool.Name, tool.Description, tool.InputSchema, tool.IsReadOnly));
        // A page of a browser has the size of its pane: its view has no window of its own to resize.
        Tools = browsers ? [.. tools.Where(static tool => tool.Name != "resize_page").OrderBy(static tool => tool.Name, StringComparer.Ordinal)] : Describe(tools);
    }

    /// <summary>
    /// Turns automation on for the pages of the browsers of the sessions, and for nothing else of the application.
    /// The tools are those of the automation as they are, <c>new_page</c> and <c>close_page</c> included: who
    /// offers them to a session says what they mean there (<see cref="Browser.DesktopBrowserTools"/>).
    /// </summary>
    /// <param name="application">The application whose browser views the tools see and drive.</param>
    /// <param name="filesDirectory">The folder the tools read their files from; created when it does not exist.</param>
    /// <exception cref="ArgumentNullException"><paramref name="application"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="filesDirectory"/> is blank.</exception>
    internal static DesktopUiAutomation ForBrowsers(NeoApplication application, string filesDirectory) => new(application, filesDirectory, browsers: true);

    /// <summary>The page of the automation that a view is, by the label of the view.</summary>
    /// <param name="viewLabel">The label the view was created with.</param>
    /// <returns>The identifier the tools take as <c>pageId</c>, or null when no page of the automation has that label.</returns>
    internal int? FindPage(string viewLabel)
    {
        ArgumentNullException.ThrowIfNull(viewLabel);
        foreach (var page in _automation.Pages)
        {
            if (string.Equals(page.View.ViewLabel, viewLabel, StringComparison.Ordinal)) return page.Id;
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// An automation takes the JavaScript dialogs of every page it drives, whoever made the page open one: the
    /// page shows nothing by itself. What the page waits in is told here, for who shows it to the user.
    /// </remarks>
    public Browser.BrowserDialog? Find(string viewLabel)
    {
        ArgumentNullException.ThrowIfNull(viewLabel);
        return Page(viewLabel)?.Dialog is { } dialog ? Browser.BrowserDialog.Create(dialog.TypeName, dialog.Message, dialog.DefaultText) : null;
    }

    /// <inheritdoc />
    public async ValueTask<bool> AnswerAsync(string viewLabel, bool accept, string? text)
    {
        ArgumentNullException.ThrowIfNull(viewLabel);
        if (Page(viewLabel) is not { } page) return false;
        try
        {
            await page.HandleDialogAsync(accept, text).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is NeoAutomationException or ObjectDisposedException)
        {
            // The dialog was answered meanwhile, by a tool or by its time running out, or the page is gone.
            return false;
        }
    }

    private NeoAutomationPage? Page(string viewLabel)
    {
        foreach (var page in _automation.Pages)
        {
            if (string.Equals(page.View.ViewLabel, viewLabel, StringComparison.Ordinal)) return page;
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<DesktopUiTool> Tools { get; }

    /// <inheritdoc />
    public string FilesDirectory { get; }

    /// <summary>
    /// The tools as the window offers them: the ones for browser windows are left out, and the texts that speak
    /// of a browser or of the folders of the automation say what holds here.
    /// </summary>
    internal static IReadOnlyList<DesktopUiTool> Describe(IEnumerable<DesktopUiTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return [.. tools.Where(static tool => tool.Name is not ("new_page" or "close_page")).Select(static tool => tool.Name switch
        {
            "list_pages" => tool with { Description = tool.Description + " The pages are the windows of CodeAlta Desktop." },
            "navigate_page" => tool with { Description = tool.Description + " The page of CodeAlta Desktop only loads the application: use the type \"reload\" to load it again." },
            "take_screenshot" => tool with { InputSchema = WithDescription(tool.InputSchema, "filePath", "The path of the file to save the screenshot to instead of attaching it to the response: absolute, or relative to the working folder of the caller.") },
            "take_snapshot" => tool with { InputSchema = WithDescription(tool.InputSchema, "filePath", "The path of the file to save the snapshot to instead of attaching it to the response: absolute, or relative to the working folder of the caller.") },
            _ => tool,
        }).OrderBy(static tool => tool.Name, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public async Task<DesktopUiToolResult> CallAsync(string name, JsonElement arguments, DesktopUiFiles files, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(files);
        if (!Tools.Any(tool => tool.Name == name)) return DesktopUiToolResult.Error($"Unknown tool \"{name}\". Call a tool of the list of tools.");

        // A file the caller names is saved where that caller may write. The automation writes into its own folder
        // only: it saves the file there, and the file is then moved to where it was asked for.
        string? target = null, staged = null;
        if (name is "take_screenshot" or "take_snapshot" && arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("filePath", out var requested) && requested.ValueKind == JsonValueKind.String)
        {
            if (!TryResolveFile(requested.GetString()!, files, out target, out var refusal)) return DesktopUiToolResult.Error(refusal);
            staged = Path.Combine(FilesDirectory, ".staged", Guid.NewGuid().ToString("N") + Path.GetExtension(target));
            arguments = WithValue(arguments, "filePath", staged);
        }

        NeoAutomationToolResult result;
        try
        {
            result = await _automation.CallToolAsync(name, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return DesktopUiToolResult.Error("The window is closing.");
        }

        var text = result.Text;
        if (staged is not null)
        {
            if (result.IsError || !File.Exists(staged)) Remove(staged);
            else
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target!)!);
                    File.Move(staged, target!, overwrite: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    Remove(staged);
                    return DesktopUiToolResult.Error($"The file could not be saved to {target}: {exception.Message}");
                }

                text = text.Replace(staged, target, StringComparison.Ordinal);
            }
        }
        else if (result.IsError && text.Contains("outside the directories this application allows for files", StringComparison.Ordinal))
        {
            // The text does not name the folder, and nothing else tells a caller where a file has to be.
            text += $" Put the file in {FilesDirectory}.";
        }

        return new DesktopUiToolResult(text, [.. result.Images.Select(static image => new DesktopUiImage(image.Data, image.ContentType))], result.IsError);
    }

    /// <summary>Turns automation off.</summary>
    public ValueTask DisposeAsync() => _automation.DisposeAsync();

    /// <summary>
    /// Resolves the file a caller wants a tool to save: a relative path starts from the caller's folder, and the
    /// file has to be inside one of the folders the caller may write to.
    /// </summary>
    /// <param name="requested">The path as the caller gave it.</param>
    /// <param name="files">Where the caller may have a file saved.</param>
    /// <param name="path">The full path of the file.</param>
    /// <param name="refusal">Why the path is refused, for the caller.</param>
    /// <returns>Whether the file may be saved there.</returns>
    internal static bool TryResolveFile(string requested, DesktopUiFiles files, [NotNullWhen(true)] out string? path, [NotNullWhen(false)] out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(files);
        path = null;
        if (string.IsNullOrWhiteSpace(requested))
        {
            refusal = "The argument \"filePath\" must be a file path.";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(requested, files.BaseDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            refusal = "The argument \"filePath\" is not a valid file path.";
            return false;
        }

        if (!files.Roots.Any(root => GitWorktreeService.IsWithin(full, root)))
        {
            refusal = $"The path of \"filePath\" is outside the folders a file can be saved in: {files.Description ?? string.Join(", ", files.Roots)}.";
            return false;
        }

        if (Directory.Exists(full))
        {
            refusal = $"\"{full}\" is a folder: \"filePath\" names a file.";
            return false;
        }

        path = full;
        refusal = null;
        return true;
    }

    // The schema of a tool with another description for one of its arguments.
    private static JsonElement WithDescription(JsonElement schema, string property, string description)
    {
        if (JsonNode.Parse(schema.GetRawText()) is not JsonObject root || root["properties"] is not JsonObject properties
            || properties[property] is not JsonObject argument) return schema;
        argument["description"] = description;
        return Clone(root);
    }

    // The arguments of a call with another value for one of them.
    private static JsonElement WithValue(JsonElement arguments, string property, string value)
    {
        var root = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        root[property] = value;
        return Clone(root);
    }

    private static JsonElement Clone(JsonObject node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static void Remove(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // It stays in the folder of the tools, which is theirs.
        }
    }
}
