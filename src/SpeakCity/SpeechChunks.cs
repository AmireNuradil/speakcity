using System.Text.RegularExpressions;

namespace SpeakCity;

/// <summary>
/// Splits one of Lucy's lines into sentences so the first one can be voiced while
/// the rest is still being synthesised. Kokoro's time grows with the length of the
/// text, so the wait before Lucy starts talking becomes the time for one sentence.
/// </summary>
public static class SpeechChunks
{
    // Pieces shorter than this ("Great!", "OK.") are joined to the next sentence: a
    // separate clip for one word adds a playback gap without saving any time.
    private const int MinimumChunk = 20;
    private static readonly Regex SentenceEnd = new(@"(?<=[.!?…])\s+", RegexOptions.Compiled);

    public static IReadOnlyList<string> Split(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        string pending = "";
        foreach (string raw in SentenceEnd.Split(text.Trim()))
        {
            string sentence = raw.Trim();
            if (sentence.Length == 0) continue;
            pending = pending.Length == 0 ? sentence : pending + " " + sentence;
            if (pending.Length >= MinimumChunk)
            {
                result.Add(pending);
                pending = "";
            }
        }
        if (pending.Length > 0)
        {
            if (result.Count > 0) result[^1] += " " + pending;
            else result.Add(pending);
        }
        return result;
    }
}
