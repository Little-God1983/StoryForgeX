using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Script;

/// <summary>
/// The whole script (board 2c): every segment in order with its narration, and Approve, Regenerate,
/// Edit text and versions on each. They do what the segment panel does: Regenerate and Edit text
/// select the segment and open their line or editor there.
/// </summary>
public sealed partial class ScriptMatrixViewModel
{
    /// <summary>"5 segments · 1:11 · 178 words".</summary>
    [ObservableProperty]
    private string _wholeScriptTotals = "";

    /// <summary>"Copied: 5 segments, 178 words." after Copy all; empty before.</summary>
    [ObservableProperty]
    private string _copiedText = "";

    /// <summary>Puts text on the clipboard; tests pass their own.</summary>
    public Action<string> CopyText { get; set; } = System.Windows.Clipboard.SetText;

    private bool CanActOnRow(SegmentRowViewModel? row) => row is not null && !IsRunning && row.State != StageState.Running;

    private bool CanApproveRow(SegmentRowViewModel? row) => CanActOnRow(row) && row!.State != StageState.Approved;

    [RelayCommand(CanExecute = nameof(CanApproveRow))]
    private Task ApproveRowAsync(SegmentRowViewModel? row) =>
        ActAsync("approve the segment", () => client.ApproveSegmentAsync(ProjectId, PipelineStage.Script, row!.Id, row.Segment.Version));

    [RelayCommand(CanExecute = nameof(CanActOnRow))]
    private void AskNoteRow(SegmentRowViewModel? row)
    {
        Select(row, PipelineStage.Script);
        AskNote();
    }

    [RelayCommand(CanExecute = nameof(CanActOnRow))]
    private void StartEditRow(SegmentRowViewModel? row)
    {
        Select(row, PipelineStage.Script);
        StartEdit();
    }

    [RelayCommand]
    private Task ShowRowVersionAsync(SegmentVersionChoice? choice) =>
        ActAsync("switch the version", () => client.SelectSegmentVersionAsync(ProjectId, PipelineStage.Script, choice!.Key!, choice.Version));

    /// <summary>The whole narration as text, a blank line between segments.</summary>
    [RelayCommand]
    private void CopyAll()
    {
        try
        {
            CopyText(string.Join(Environment.NewLine + Environment.NewLine, Rows.Select(r => r.Narration)));
            CopiedText = $"Copied: {Rows.Count} segments, {Rows.Sum(r => r.Words)} words.";
            ActionError = null;
        }
        catch (Exception ex)
        {
            // The clipboard is held by another program now and then.
            Debug.WriteLine($"Could not copy the script: {ex}");
            ActionError = $"Could not copy the script: {ex.Message}";
        }
    }

    partial void OnIsAskingNoteChanged(bool value) => RefreshRowModes();

    partial void OnIsEditingChanged(bool value) => RefreshRowModes();

    /// <summary>The note line and the editor show at the selected segment in the whole script too.</summary>
    private void RefreshRowModes()
    {
        foreach (var row in Rows)
        {
            row.IsAskingNote = IsAskingNote && IsScriptPanel && row == Selected;
            row.IsEditing = IsEditing && row == Selected;
        }
    }

    private void RefreshRowCommands()
    {
        ApproveRowCommand.NotifyCanExecuteChanged();
        AskNoteRowCommand.NotifyCanExecuteChanged();
        StartEditRowCommand.NotifyCanExecuteChanged();
    }
}
