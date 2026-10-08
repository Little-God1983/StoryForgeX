using System.Text.RegularExpressions;
using StoryForge.Client;

namespace StoryForge.Engine.Voice;

/// <summary>
/// Cuts a narration into parts the TTS workflow speaks well: audio longer than about a minute goes
/// bad, so no part is estimated above <see cref="VoiceClip.MaxPartSeconds"/>. Cuts fall between
/// sentences; a sentence too long on its own is cut at a comma, a colon or a dash, and only as a
/// last resort between words.
/// </summary>
internal static partial class NarrationParts
{
    public static IReadOnlyList<string> Split(string narration, string language, double maxSeconds = VoiceClip.MaxPartSeconds)
    {
        var wordsPerPart = Math.Max(1, (int)Math.Floor(maxSeconds * ScriptSheet.WordsPerMinute(language) / 60.0));
        var pieces = Sentences().Split(narration.Trim())
            .Where(s => s.Length > 0)
            .SelectMany(s => Words(s) <= wordsPerPart ? [s] : Shorter(s, wordsPerPart));

        var parts = new List<string>();
        var current = "";
        foreach (var piece in pieces)
        {
            var joined = current.Length == 0 ? piece : current + " " + piece;
            if (current.Length > 0 && Words(joined) > wordsPerPart)
            {
                parts.Add(current);
                joined = piece;
            }
            current = joined;
        }
        if (current.Length > 0)
        {
            parts.Add(current);
        }
        return parts;
    }

    /// <summary>A sentence too long for one part: at its pauses first, then in runs of words.</summary>
    private static IEnumerable<string> Shorter(string sentence, int wordsPerPart)
    {
        foreach (var clause in Pauses().Split(sentence).Where(c => c.Length > 0))
        {
            var words = clause.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < words.Length; i += wordsPerPart)
            {
                yield return string.Join(' ', words.Skip(i).Take(wordsPerPart));
            }
        }
    }

    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // After . ! ? or … (and a closing quote or bracket), before the next word.
    [GeneratedRegex(@"(?<=[.!?…][""'”’)\]]?)\s+")]
    private static partial Regex Sentences();

    // After , ; : or a dash that stands between words.
    [GeneratedRegex(@"(?<=[,;:])\s+|\s+(?=[–—]\s)")]
    private static partial Regex Pauses();
}
