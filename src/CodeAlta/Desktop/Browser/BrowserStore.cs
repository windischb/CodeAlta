using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Desktop.Browser;

/// <summary>A page of the browser of a session, as it is kept from one run of the application to the next.</summary>
/// <param name="Id">The identifier of the page.</param>
/// <param name="Address">The address it showed last.</param>
/// <param name="Title">The title it showed last.</param>
internal sealed record BrowserPageRecord(string Id, string Address, string Title);

/// <summary>The pages of the browser of a session, as they are kept.</summary>
/// <param name="Pages">The pages, in the order of their strip.</param>
/// <param name="Active">The identifier of the page in front, or null.</param>
internal sealed record BrowserSessionRecord(IReadOnlyList<BrowserPageRecord> Pages, string? Active);

/// <summary>Keeps the pages of the browser of each session.</summary>
internal interface IBrowserStore
{
    /// <summary>Reads the pages of a session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The pages, or null when none are kept or they cannot be read.</returns>
    BrowserSessionRecord? Load(string sessionId);

    /// <summary>Keeps the pages of a session. A session without pages keeps nothing.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="record">Its pages.</param>
    void Save(string sessionId, BrowserSessionRecord record);

    /// <summary>Removes everything kept for the browser of a session.</summary>
    /// <param name="sessionId">The session.</param>
    void Delete(string sessionId);
}

/// <summary>
/// Keeps the pages of the browser of each session in a folder of the state of this instance:
/// <c>&lt;root&gt;/&lt;session id&gt;/pages.json</c>. What cannot be read is as if it were not there, and what cannot be
/// written is not kept: the browser works without it.
/// </summary>
/// <param name="root">The folder of the browsers; created when something is first kept.</param>
internal sealed class BrowserFileStore(string root) : IBrowserStore
{
    /// <summary>The largest file that is read, in bytes.</summary>
    internal const int MaximumFileBytes = 256 * 1024;

    private const string FileName = "pages.json";
    private readonly string _root = Path.GetFullPath(root);

    /// <summary>The folder of a session, or null for an identifier that cannot name a folder.</summary>
    /// <param name="sessionId">The session.</param>
    internal string? Folder(string sessionId)
        => sessionId is { Length: > 0 and <= 128 } && sessionId.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') && sessionId.Trim('.').Length > 0
            ? Path.Combine(_root, sessionId)
            : null;

    /// <inheritdoc />
    public BrowserSessionRecord? Load(string sessionId)
    {
        if (Folder(sessionId) is not { } folder) return null;
        try
        {
            var file = new FileInfo(Path.Combine(folder, FileName));
            if (!file.Exists || file.Length > MaximumFileBytes) return null;
            return JsonSerializer.Deserialize(File.ReadAllBytes(file.FullName), BrowserStoreJsonContext.Default.BrowserSessionRecord);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Save(string sessionId, BrowserSessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (Folder(sessionId) is not { } folder) return;
        try
        {
            // A session without pages keeps none; what else is kept in its folder, its annotations, stays.
            if (record.Pages.Count == 0)
            {
                File.Delete(Path.Combine(folder, FileName));
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                return;
            }

            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, FileName);
            var staged = file + ".tmp";
            File.WriteAllBytes(staged, JsonSerializer.SerializeToUtf8Bytes(record, BrowserStoreJsonContext.Default.BrowserSessionRecord));
            File.Move(staged, file, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The pages are then not there at the next start.
        }
    }

    /// <inheritdoc />
    public void Delete(string sessionId)
    {
        if (Folder(sessionId) is not { } folder) return;
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // What stays is read again, or replaced, when the session next opens its browser.
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BrowserSessionRecord))]
[JsonSerializable(typeof(string))]
internal sealed partial class BrowserStoreJsonContext : JsonSerializerContext;
