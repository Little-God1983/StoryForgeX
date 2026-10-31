using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StoryForge.App.ViewModels.Assembly;

/// <summary>
/// The Assembly screen's way to an editor (#71): the timeline file beside the video, Open folder,
/// and Send to Resolve with the project name you give it (the StoryForge X project's to start with).
/// </summary>
public sealed partial class AssemblyViewModel
{
    /// <summary>The shown version's FCPXML timeline; null when none was written or the file is gone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimeline))]
    [NotifyCanExecuteChangedFor(nameof(OpenTimelineFolderCommand), nameof(SendToResolveCommand))]
    private string? _timelinePath;

    public bool HasTimeline => TimelinePath is not null;

    /// <summary>The new Resolve project's name.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendToResolveCommand))]
    private string _resolveProjectName = projectName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendToResolveCommand))]
    private bool _isSending;

    /// <summary>"Sending… this can take a minute if Resolve is not open yet", then what came of it; empty before.</summary>
    [ObservableProperty]
    private string _resolveText = "";

    /// <summary>The last send did not get the timeline into Resolve.</summary>
    [ObservableProperty]
    private bool _resolveFailed;

    private bool CanOpenTimelineFolder() => TimelinePath is not null;

    [RelayCommand(CanExecute = nameof(CanOpenTimelineFolder))]
    private void OpenTimelineFolder() => _open(System.IO.Path.GetDirectoryName(TimelinePath!)!);

    private bool CanSendToResolve() => TimelinePath is not null && Version is not null && ResolveProjectName.Trim().Length > 0 && !IsSending && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanSendToResolve))]
    private async Task SendToResolveAsync()
    {
        var name = ResolveProjectName.Trim();
        IsSending = true;
        ResolveFailed = false;
        ResolveText = "Sending… this can take a minute if Resolve is not open yet.";
        try
        {
            var sent = await client.SendToResolveAsync(ProjectId, Version!.Value, name);
            ResolveFailed = !sent.Ok;
            ResolveText = sent.Ok
                ? $"In Resolve as \"{name}\": timeline \"{sent.TimelineName ?? name}\" with {sent.Clips} clips.{(sent.Problem is { } note ? " " + note : "")}"
                : sent.Problem ?? $"Resolve stopped at \"{sent.Stage}\".";
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            ResolveFailed = true;
            ResolveText = ex.Message;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Send to Resolve failed: {ex}");
            ResolveFailed = true;
            ResolveText = $"Could not send to Resolve: {ex.Message}";
        }
        finally
        {
            IsSending = false;
        }
    }
}
