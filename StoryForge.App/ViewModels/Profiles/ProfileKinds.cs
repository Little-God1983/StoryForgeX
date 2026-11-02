using StoryForge.App.ViewModels.Settings;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Profiles;

/// <summary>How the screens name each kind of profile, as on the design canvas.</summary>
public static class ProfileKinds
{
    public static IReadOnlyList<Choice<ProfileKind>> Choices { get; } =
        [.. Enum.GetValues<ProfileKind>().Select(k => new Choice<ProfileKind>(k, Label(k)))];

    public static string Label(ProfileKind kind) => kind switch
    {
        ProfileKind.Research => "Research (LLM)",
        ProfileKind.Script => "Script (LLM)",
        ProfileKind.Storyboard => "Storyboard (LLM)",
        ProfileKind.Image => "Image",
        ProfileKind.Video => "Video",
        ProfileKind.Voice => "Voice",
        _ => kind.ToString(),
    };

    /// <summary>
    /// Every value a kind's stage can put into its workflow, as the engine's new profiles list them.
    /// A profile saved before a stage learned one (a video profile from before Clips) gets its row on edit.
    /// </summary>
    public static IReadOnlyList<string> InputKeys(ProfileKind kind) => kind switch
    {
        ProfileKind.Image => [.. MediaKeys, "images"],
        ProfileKind.Video => [.. MediaKeys, "image", "frames", "fps"],
        ProfileKind.Voice => ["text", "instruction", "reference_audio", "seed"],
        _ => [],
    };

    private static readonly string[] MediaKeys = ["prompt", "negative", "width", "height", "seed", "steps"];
}
