using StoryForge.Client;

namespace StoryForge.Engine.Voice;

/// <summary>
/// When each word is heard, estimated: the TTS workflow gives audio and no timings. Each part's
/// measured length is shared out over its words by their length, with a pause after punctuation and
/// a little silence at either end. Every part is measured, so an estimate can only drift within its
/// part (at most about 45 s). The weights below are guesses to tune by listening.
/// </summary>
internal static class WordTimings
{
    /// <summary>The weights the estimate uses; one place to change while experimenting.</summary>
    /// <param name="EdgeSeconds">Silence the TTS leaves at the start and at the end of a part.</param>
    /// <param name="MinLetters">The least a word counts for: "a" takes longer than one letter's share.</param>
    /// <param name="CommaPause">A pause after , ; : or a dash, in letters.</param>
    /// <param name="SentencePause">A pause after . ! ? or …, in letters.</param>
    internal sealed record Weights(double EdgeSeconds, int MinLetters, double CommaPause, double SentencePause)
    {
        public static Weights Default { get; } = new(EdgeSeconds: 0.15, MinLetters: 2, CommaPause: 3, SentencePause: 6);
    }

    public static IReadOnlyList<SpokenWord> Estimate(IReadOnlyList<VoicePart> parts, Weights? weights = null)
    {
        weights ??= Weights.Default;
        var words = new List<SpokenWord>();
        var offset = 0.0;
        foreach (var part in parts)
        {
            var tokens = Words(part.Text);
            if (tokens.Count == 0)
            {
                offset += part.Seconds;
                continue;
            }
            var edge = Math.Min(weights.EdgeSeconds, part.Seconds / 4);
            var speaking = Math.Max(0, part.Seconds - 2 * edge);
            var spoken = tokens.Select(t => Math.Max(weights.MinLetters, t.Count(char.IsLetterOrDigit))).ToList();
            var pauses = tokens.Select(t => Pause(t, weights)).ToList();
            pauses[^1] = 0;   // the part's own end is the edge silence
            var total = spoken.Sum() + pauses.Sum();
            var at = offset + edge;
            for (var i = 0; i < tokens.Count; i++)
            {
                var length = speaking * spoken[i] / total;
                words.Add(new SpokenWord(tokens[i], Round(at), Round(at + length)));
                at += length + speaking * pauses[i] / total;
            }
            offset += part.Seconds;
        }
        return words;
    }

    /// <summary>The part's words; a dash standing between words goes with the word before it, as its pause.</summary>
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (words.Count > 0 && !token.Any(char.IsLetterOrDigit))
            {
                words[^1] += " " + token;
            }
            else
            {
                words.Add(token);
            }
        }
        return words;
    }

    private static double Pause(string token, Weights weights)
    {
        var end = token.TrimEnd('"', '\'', '”', '’', ')', ']');
        return end.Length == 0 ? 0 : end[^1] switch
        {
            '.' or '!' or '?' or '…' => weights.SentencePause,
            ',' or ';' or ':' or '–' or '—' => weights.CommaPause,
            _ => 0,
        };
    }

    private static double Round(double seconds) => Math.Round(seconds, 3);
}
