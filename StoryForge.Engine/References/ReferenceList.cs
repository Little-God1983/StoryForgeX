using StoryForge.Client;

namespace StoryForge.Engine.References;

/// <summary>
/// Who and what gets a reference: only what the storyboard shows, in two or more shots, or in one
/// when it carries the story. The rest of the cast is listed with how often it is shown, so you can
/// make one a reference after all.
/// </summary>
internal static class ReferenceList
{
    public static (IReadOnlyList<Reference> References, IReadOnlyList<OtherCast> Others) Propose(StoryboardSheet sheet, IReadOnlyList<Shot> shots)
    {
        // The cast list first, then anyone put in a shot by hand, in the order the shots show them.
        var cast = sheet.Cast.ToList();
        foreach (var name in shots.SelectMany(s => s.Cast).Distinct().Where(n => cast.All(c => !Same(c.Name, n))))
        {
            cast.Add(new CastMember(name, CastKind.Character, ""));
        }
        var references = new List<Reference>();
        var others = new List<OtherCast>();
        foreach (var member in cast)
        {
            var shown = shots.Where(s => s.Cast.Any(n => Same(n, member.Name))).Select(s => s.Key).ToList();
            if (shown.Count >= 2 || (shown.Count == 1 && member.KeyReason.Length > 0))
            {
                references.Add(new Reference(IdOf(references.Count), member.Name, member.Kind, member.KeyReason, "", shown, []));
            }
            else
            {
                others.Add(new OtherCast(member.Name, member.Kind, shown.Count));
            }
        }
        return (references, others);
    }

    /// <summary>"R01" for the first reference.</summary>
    public static string IdOf(int index) => $"R{index + 1:00}";

    /// <summary>A new reference's id: one past the highest in use.</summary>
    public static string NextId(IEnumerable<Reference> references) =>
        IdOf(references.Select(r => int.TryParse(r.Id.AsSpan(1), out var n) ? n : 0).DefaultIfEmpty(0).Max());

    public static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
