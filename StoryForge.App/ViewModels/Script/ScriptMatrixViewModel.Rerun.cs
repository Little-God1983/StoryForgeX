using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Script;

/// <summary>
/// Rerun pipeline from here ↓ (board 2b's panel): the selected cell made again, then stage by stage
/// what is made from it and nothing else, each waiting at its gate. The question before it says
/// what it would make again; a cell the rerun waits on says so, with Stop rerun.
/// </summary>
public sealed partial class ScriptMatrixViewModel
{
    /// <summary>A rerun waits on the selected cell: once it is approved, what is made from it is made again.</summary>
    public bool SelectedIsRerun =>
        (IsBoardPanel ? SelectedShot?.Shot.Rerun : IsStillPanel ? false : IsVoicePanel ? SelectedVoice?.Rerun : Selected?.Segment.Rerun) == true;

    /// <summary>"Part of a rerun: once you approve it, what is made from it is made again."</summary>
    public string SelectedRerunText => SelectedState == StageState.Approved
        ? "Part of a rerun: what is made from it is made again next."
        : "Part of a rerun: once you approve it, what is made from it is made again.";

    /// <summary>Asks first with what it would make again; nothing happens without a yes.</summary>
    [RelayCommand(CanExecute = nameof(SegmentIdle))]
    private async Task RerunFromHereAsync()
    {
        var (stage, key) = (SelectedStage, SelectedKey);
        RerunPlan plan;
        try
        {
            plan = await client.GetRerunPlanAsync(ProjectId, stage, key);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not rerun the pipeline from here: {ex}");
            ActionError = $"Could not rerun the pipeline from here: {ex.Message}";
            return;
        }
        if (!plan.CanRun)
        {
            ActionError = plan.Text;   // nothing to ask: nothing would be made
            return;
        }
        if (confirm?.Invoke($"{plan.Text} Go on?") == false)
        {
            return;
        }
        await ActAsync("rerun the pipeline from here", () => client.RerunFromHereAsync(ProjectId, stage, key));
    }

    [RelayCommand]
    private Task StopRerunAsync() => ActAsync("stop the rerun", () => client.StopRerunAsync(ProjectId));
}
