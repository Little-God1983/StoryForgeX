using StoryForge.Engine.Storyboard;

namespace StoryForge.Engine.Tests;

/// <summary>Storyboard answers as tests write them.</summary>
internal static class StoryboardAnswers
{
    /// <summary>
    /// The answer with "no earlier still fits" for every cast member seen before and not answered
    /// for yet, as the check wants: for tests about something else.
    /// </summary>
    /// <param name="seenBefore">Who the shots outside this answer show before it.</param>
    public static StoryboardOutput WithPicks(this StoryboardOutput output, params string[] seenBefore)
    {
        var seen = seenBefore.Select(Normal).ToHashSet();
        return output with
        {
            Segments = [.. output.Segments.Select(segment => segment is null ? segment! : segment with
            {
                Shots = [.. segment.Shots.Select(shot =>
                {
                    if (shot is null)
                    {
                        return shot!;
                    }
                    var answered = shot.EarlierStills.Select(p => Normal(p.Cast)).ToHashSet();
                    var picks = shot.EarlierStills.Concat(shot.Cast.Where(n => seen.Contains(Normal(n)) && !answered.Contains(Normal(n))).Select(n => new EarlierStillOutput(n, ""))).ToList();
                    seen.UnionWith(shot.Cast.Select(Normal));
                    return shot with { EarlierStills = picks };
                })],
            })],
        };
    }

    private static string Normal(string name) => string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
