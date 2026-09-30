using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpeakCity;

/// <summary>
/// "My vocabulary" on the Feedback page: words the learner saved from a review, kept on this PC.
/// One entry per word, newest first and capped; an unreadable file or entry is skipped.
/// </summary>
public sealed class VocabularyStore
{
    public const int MaxEntries = 500;
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private readonly string _path;
    private readonly List<JsonObject> _entries = new();

    public VocabularyStore(string path)
    {
        _path = path;
        Load();
    }

    public static string DefaultPath => Path.Combine(AppStartup.DataRoot, "vocabulary.json");

    public IReadOnlyList<JsonObject> Entries => _entries;

    public bool Contains(string word) => _entries.Any(entry => Same(entry, word));

    /// <summary>Saves a word from a review; saving it again changes nothing.</summary>
    public void Add(JsonObject word, string scenario, string title, DateTime savedUtc)
    {
        string label = Text(word["word"])?.Trim() ?? "";
        if (label.Length == 0 || Contains(label)) return;
        _entries.Insert(0, new JsonObject
        {
            ["word"] = label,
            ["meaning"] = word["meaning"] is JsonObject meaning ? meaning.DeepClone() : new JsonObject(),
            ["example"] = Text(word["example"]) ?? "",
            ["scenario"] = scenario,
            ["title"] = title,
            ["saved_utc"] = savedUtc.ToUniversalTime().ToString("O")
        });
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        Save();
    }

    public bool Remove(string word)
    {
        int removed = _entries.RemoveAll(entry => Same(entry, word));
        if (removed > 0) Save();
        return removed > 0;
    }

    private void Load()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length is 0 or > MaxFileBytes) return;
            if (JsonNode.Parse(File.ReadAllText(_path)) is not JsonArray items) return;
            foreach (var item in items)
                if (item is JsonObject entry && Text(entry["word"]) is { Length: > 0 } word && entry["meaning"] is JsonObject
                    && !Contains(word) && _entries.Count < MaxEntries)
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

    private static bool Same(JsonObject entry, string word) =>
        string.Equals(Text(entry["word"]), word.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
