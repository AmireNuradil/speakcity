using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpeakCity;

/// <summary>
/// The learner's own choices (English level, how feedback is given). They are not secrets, so they
/// are plain JSON in the same per-user folder as the encrypted API settings. Anything unreadable or
/// unknown falls back to the default instead of stopping the app.
/// </summary>
public sealed class AppPreferences
{
    public static readonly IReadOnlyList<string> Levels = ["A1", "A2", "B1", "B2", "C1", "C2"];
    public static readonly IReadOnlyList<string> Languages = ["en", "kk", "ru"];
    public static readonly IReadOnlyList<string> Depths = ["focused", "thorough"];
    private const int MaxFileBytes = 16 * 1024;

    public string Level { get; set; } = "A2";
    public string FeedbackLanguage { get; set; } = "en";
    public string FeedbackDepth { get; set; } = "focused";

    public static string DefaultPath => Path.Combine(AppStartup.DataRoot, "preferences.json");

    public static AppPreferences Load(string path)
    {
        var result = new AppPreferences();
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > MaxFileBytes) return result;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject data) return result;
            result.Level = Pick(data["level"], Levels, result.Level);
            result.FeedbackLanguage = Pick(data["feedback_language"], Languages, result.FeedbackLanguage);
            result.FeedbackDepth = Pick(data["feedback_depth"], Depths, result.FeedbackDepth);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return result;
    }

    public void Save(string path)
    {
        var data = new JsonObject { ["level"] = Level, ["feedback_language"] = FeedbackLanguage, ["feedback_depth"] = FeedbackDepth };
        AtomicFile.WriteAllText(path, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Pick(JsonNode? node, IReadOnlyList<string> allowed, string fallback) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && allowed.Contains(text) ? text : fallback;
}

internal static class AtomicFile
{
    /// <summary>Written through a temporary file, so a crash mid-write cannot leave half a file behind.</summary>
    public static void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }
}
