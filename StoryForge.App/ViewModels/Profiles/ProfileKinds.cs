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
}
