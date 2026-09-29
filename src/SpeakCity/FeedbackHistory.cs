using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpeakCity;

/// <summary>
/// Finished reviews kept on this PC for the Feedback page: the corrections and the place's words,
/// never the whole conversation. Newest first and capped; an unreadable file or entry is skipped
/// rather than stopping the app.
/// </summary>
public sealed class FeedbackHistory
{
    public const int MaxEntries = 50;
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private static readonly string[] CorrectionFields = ["original", "corrected", "explanation"];
    private static readonly string[] WordFields = ["word", "meaning", "example", "encountered", "used_by_learner"];
    private readonly string _path;
    private readonly List<JsonObject> _entries = new();

    public FeedbackHistory(string path)
    {
        _path = path;
        Load();
    }

    public static string DefaultPath => Path.Combine(AppStartup.DataRoot, "feedback-history.json");

    public IReadOnlyList<JsonObject> Entries => _entries;

    /// <summary>Keeps one finished review. Only known fields are copied out of the router's payload.</summary>
    public void Add(JsonObject review, string title, string level, DateTime finishedUtc)
    {
        _entries.Insert(0, new JsonObject
        {
            ["finished_utc"] = finishedUtc.ToUniversalTime().ToString("O"),
            ["scenario"] = Text(review["scenario"]) ?? "",
            ["title"] = title,
            ["level"] = level,
            ["corrections"] = Copy(review["corrections"], CorrectionFields),
            ["words"] = Copy(review["vocabulary"], WordFields)
        });
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        Save();
    }

    public void Clear()
    {
        _entries.Clear();
        Save();
    }

    private void Load()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length is 0 or > MaxFileBytes) return;
            if (JsonNode.Parse(File.ReadAllText(_path)) is not JsonArray items) return;
            foreach (var item in items)
                if (item is JsonObject entry && Text(entry["scenario"]) is not null
                    && entry["corrections"] is JsonArray && entry["words"] is JsonArray && _entries.Count < MaxEntries)
                    _entries.Add((JsonObject)entry.DeepClone());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void Save()
    {
        var items = new JsonArray();
        foreach (var entry in _entries) items.Add(entry.DeepClone());
        AtomicFile.WriteAllText(_path, items.ToJsonString());
    }

    private static JsonArray Copy(JsonNode? source, string[] fields)
    {
        var result = new JsonArray();
        if (source is not JsonArray items) return result;
        foreach (var item in items)
        {
            if (item is not JsonObject entry) continue;
            var copy = new JsonObject();
            foreach (string field in fields)
                if (entry[field] is { } value) copy[field] = value.DeepClone();
            result.Add(copy);
        }
        return result;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
