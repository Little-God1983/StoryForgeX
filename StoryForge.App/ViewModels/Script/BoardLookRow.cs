namespace StoryForge.App.ViewModels.Script;

/// <summary>Someone's look on the Board panel.</summary>
/// <param name="Shots">How many shots show them: a change holds for all of them.</param>
/// <param name="ReadOnly">They have a reference, whose Look box owns the look.</param>
public sealed record BoardLookRow(string Name, string Look, int Shots, bool ReadOnly)
{
    public string Title => $"{Name} \u00b7 in {Shots} {(Shots == 1 ? "shot" : "shots")}";

    public bool IsEditable => !ReadOnly;
}

/// <summary>A picture a still was painted with: its file when it is there, and which shot it is.</summary>
public sealed record StillSourceRow(string? File, string Caption);
