using StoryForge.Client;

namespace StoryForge.App.ViewModels;

/// <summary>
/// A failed stage's way out of a profile fixed after the project started (#75): the newest version of
/// the profile the stage was started with, offered on its failure line.
/// </summary>
internal static class NewerProfileOffer
{
    /// <summary>"Use Slow cinematic push v2"; null when the stage's profile has no newer version, or it cannot be told.</summary>
    public static async Task<string?> OfferAsync(IStoryForgeClient client, Guid projectId, PipelineStage stage)
    {
        try
        {
            var setup = (await client.GetProjectAsync(projectId)).Setup;
            var own = stage switch
            {
                PipelineStage.Voice => setup.Voice.Profile,
                PipelineStage.References => setup.References.Profile,
                PipelineStage.Stills => setup.Stills.Profile,
                PipelineStage.Clips => setup.Clips.Profile,
                _ => null,
            };
            var profile = (await client.GetProfilesAsync()).FirstOrDefault(p => p.Id == own?.ProfileId);
            return profile is not null && profile.LatestVersion > own!.Version ? $"Use {profile.Name} v{profile.LatestVersion}" : null;
        }
        catch (Exception)
        {
            return null;   // only an offer: without it the failure line still has Retry
        }
    }

    /// <summary>"Slow cinematic push v2" from "Use Slow cinematic push v2", for the error when it does not go through.</summary>
    public static string Named(string offer) => offer.StartsWith("Use ", StringComparison.Ordinal) ? offer[4..] : offer;
}
