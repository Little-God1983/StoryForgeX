using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Script;

/// <summary>
/// The failure lines of Voice, Stills and Clips offer the newer version of the profile the project
/// started with (#75): one click moves the stage to it and runs it again, as the line's own button does.
/// </summary>
public sealed partial class ScriptMatrixViewModel
{
    /// <summary>"Use Amira – voice direction v2" while the voice failed and its profile has a newer version; null otherwise.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseNewerVoiceProfileCommand))]
    private string? _voiceNewerProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseNewerStillsProfileCommand))]
    private string? _stillsNewerProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseNewerClipsProfileCommand))]
    private string? _clipsNewerProfile;

    /// <summary>What a failed stage could move to; asked only for a stage that failed.</summary>
    private async Task LoadNewerProfilesAsync()
    {
        VoiceNewerProfile = IsVoiceFailed ? await NewerProfileOffer.OfferAsync(client, ProjectId, PipelineStage.Voice) : null;
        StillsNewerProfile = IsStillsFailed ? await NewerProfileOffer.OfferAsync(client, ProjectId, PipelineStage.Stills) : null;
        ClipsNewerProfile = IsClipsFailed ? await NewerProfileOffer.OfferAsync(client, ProjectId, PipelineStage.Clips) : null;
    }

    private bool CanUseNewerVoiceProfile() => VoiceNewerProfile is not null && IsVoiceFailed;

    [RelayCommand(CanExecute = nameof(CanUseNewerVoiceProfile))]
    private Task UseNewerVoiceProfileAsync() =>
        UseNewerAsync(VoiceNewerProfile!, PipelineStage.Voice, () => client.RegenerateAsync(ProjectId, PipelineStage.Voice));

    private bool CanUseNewerStillsProfile() => StillsNewerProfile is not null && IsStillsFailed;

    /// <summary>Then Continue pipeline, as the Stills line's button: the stills made stay.</summary>
    [RelayCommand(CanExecute = nameof(CanUseNewerStillsProfile))]
    private Task UseNewerStillsProfileAsync() =>
        UseNewerAsync(StillsNewerProfile!, PipelineStage.Stills, () => client.ContinuePipelineAsync(ProjectId));

    private bool CanUseNewerClipsProfile() => ClipsNewerProfile is not null && IsClipsFailed;

    /// <summary>Then Make clips, as the Clips line's button: the clips made stay.</summary>
    [RelayCommand(CanExecute = nameof(CanUseNewerClipsProfile))]
    private Task UseNewerClipsProfileAsync() =>
        UseNewerAsync(ClipsNewerProfile!, PipelineStage.Clips, () => client.MakeClipsAsync(ProjectId));

    /// <summary>
    /// Nothing runs again when the move does not go through: the newer version may not run either.
    /// Once it went through the screen shows it, also when the stage then would not start.
    /// </summary>
    private async Task UseNewerAsync(string offer, PipelineStage stage, Func<Task> runAgain)
    {
        var named = NewerProfileOffer.Named(offer);
        try
        {
            await client.UseNewerProfileAsync(ProjectId, stage);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not use {named}: {ex}");
            ActionError = $"Could not use {named}: {ex.Message}";
            await LoadAsync();   // a move made elsewhere meanwhile takes its offer off the line
            return;
        }
        try
        {
            await runAgain();
            ActionError = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{named} did not start: {ex}");
            ActionError = $"Now on {named}, but it did not start again: {ex.Message}";
        }
        await LoadAsync();
    }
}
