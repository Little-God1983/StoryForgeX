using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryForge.Client;
using StoryForge.Engine.Assembly;
using StoryForge.Engine.Clips;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Resolve;

namespace StoryForge.Engine.Tests;

/// <summary>
/// #71: the Assembly also writes the cut as an FCPXML timeline beside the video, and Send to Resolve
/// hands that file to Resolve as a new project with the name you give it.
/// </summary>
public sealed partial class StillsRunTests
{
    private readonly FakeResolveSenders _resolve = new();

    private Task<IStoryForgeClient> StartWithResolveAsync() => StartAsync(services =>
    {
        services.Replace(ServiceDescriptor.Singleton<IClipPromptAgent>(_motion));
        services.Replace(ServiceDescriptor.Singleton<ICutListAgent>(_cuts));
        services.Replace(ServiceDescriptor.Singleton<IVideoRenderer>(_renderer));
        services.Replace(ServiceDescriptor.Singleton<IResolveSenders>(_resolve));
        services.AddSingleton<IStageWorker, ClipsStage>();
        services.AddSingleton<IStageWorker, AssemblyStage>();
    });

    [Fact]
    public async Task The_video_comes_with_its_cut_as_a_timeline_beside_it_named_after_the_project()
    {
        var client = await StartWithResolveAsync();
        var project = await ClipsApprovedAsync(client);

        var view = await AssemblyAsync(client, project.Id);

        Assert.Null(view.Error);
        Assert.Equal(Path.ChangeExtension(view.Video!.File, ".fcpxml"), view.Video.Timeline);
        Assert.Equal(Path.ChangeExtension(view.Path, ".fcpxml"), view.TimelinePath);
        Assert.True(File.Exists(view.TimelinePath));
        var (output, name) = Assert.Single(_renderer.Timelines);
        Assert.Equal((view.TimelinePath, project.Setup.Name), (output, name));
        Assert.Contains(view.Activity, line => line.Text == "timeline written for Resolve (FCPXML)");
    }

    [Fact]
    public async Task With_no_timeline_export_set_only_the_video_is_made()
    {
        var client = await StartWithResolveAsync();
        var settings = await client.GetSettingsAsync();
        await client.SaveSettingsAsync(settings with { Ffmpeg = settings.Ffmpeg with { TimelineExport = TimelineExport.None } });
        var project = await ClipsApprovedAsync(client);

        var view = await AssemblyAsync(client, project.Id);

        Assert.Equal(StageState.NeedsReview, view.State);
        Assert.Null(view.Video!.Timeline);
        Assert.Null(view.TimelinePath);
        Assert.Empty(_renderer.Timelines);
    }

    [Fact]
    public async Task A_timeline_that_cannot_be_written_is_said_and_the_video_still_stands()
    {
        _renderer.TimelineFailure = "ffmpeg could not read 'S01-voice.wav': Invalid data";
        var client = await StartWithResolveAsync();
        var project = await ClipsApprovedAsync(client);

        var view = await AssemblyAsync(client, project.Id);

        Assert.Equal((StageState.NeedsReview, (string?)null), (view.State, view.Error));
        Assert.True(File.Exists(view.Path));
        Assert.Null(view.Video!.Timeline);
        Assert.Contains(view.Activity, line => line.Text == "the timeline file could not be written: ffmpeg could not read 'S01-voice.wav': Invalid data");
    }

    /// <summary>Review of #71: only file errors were caught, so a name XML cannot hold (a pasted
    /// control character) failed the whole stage after the video had rendered.</summary>
    [Fact]
    public async Task Any_failure_writing_the_timeline_leaves_the_video_standing()
    {
        _renderer.TimelineCrash = new ArgumentException("'\u0001', hexadecimal value 0x01, is an invalid character.");
        var client = await StartWithResolveAsync();
        var project = await ClipsApprovedAsync(client);

        var view = await AssemblyAsync(client, project.Id);

        Assert.Equal((StageState.NeedsReview, (string?)null), (view.State, view.Error));
        Assert.Null(view.Video!.Timeline);
        Assert.Contains(view.Activity, line => line.Text.StartsWith("the timeline file could not be written: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Send_to_Resolve_hands_the_versions_timeline_over_under_the_name_given_at_the_videos_rate()
    {
        var client = await StartWithResolveAsync();
        var project = await ClipsApprovedAsync(client);
        var view = await AssemblyAsync(client, project.Id);
        _resolve.Answer = new SendToResolveResult(true, "done", null, "Soul Coins – BG3 lore", 59, 0);

        var sent = await client.SendToResolveAsync(project.Id, 1, "  Dark Eye cut  ");

        Assert.Equal(new SentToResolve(true, "done", null, "Soul Coins – BG3 lore", 59), sent);
        var (install, request) = Assert.Single(_resolve.Sends);
        Assert.Equal(("Dark Eye cut", view.TimelinePath, "24", false), (request.ProjectName, request.TimelinePath, request.FrameRate, request.PlaceMarkers));
        // Settings → Resolve empty: the standard install.
        Assert.Equal(ResolveInstall.Locate(null, null), install);
    }

    [Fact]
    public async Task Send_to_Resolve_uses_the_install_set_in_settings_and_passes_its_failure_on()
    {
        var client = await StartWithResolveAsync();
        var settings = await client.GetSettingsAsync();
        await client.SaveSettingsAsync(settings with { Resolve = new ResolveSettings(@"D:\Resolve\Resolve.exe", "\"D:\\Resolve\\py.exe\"") });
        var project = await ClipsApprovedAsync(client);
        await AssemblyAsync(client, project.Id);
        _resolve.Answer = new SendToResolveResult(false, "create", "There is already a project called 'Dark Eye' in Resolve - nothing was created.", null, 0, 0);

        var sent = await client.SendToResolveAsync(project.Id, 1, "Dark Eye");

        Assert.Equal(new SentToResolve(false, "create", "There is already a project called 'Dark Eye' in Resolve - nothing was created.", null, 0), sent);
        Assert.Equal(new ResolveInstall(@"D:\Resolve\Resolve.exe", @"D:\Resolve\py.exe"), Assert.Single(_resolve.Sends).Install);
    }

    [Fact]
    public async Task A_version_without_a_timeline_file_is_not_sent_and_says_how_to_get_one()
    {
        var client = await StartWithResolveAsync();
        var project = await ClipsApprovedAsync(client);
        var view = await AssemblyAsync(client, project.Id);
        File.Delete(view.TimelinePath!);

        var sent = await client.SendToResolveAsync(project.Id, 1, "Dark Eye");

        Assert.Equal((false, "timeline"), (sent.Ok, sent.Stage));
        Assert.Contains("Also export timeline", sent.Problem);
        Assert.Empty(_resolve.Sends);
        Assert.Null((await client.GetAssemblyAsync(project.Id)).TimelinePath);
    }

    /// <summary>Review of #71, round 3: the Assembly screen is built anew each time it opens, so its
    /// own "sending" did not stop a second send after leaving and coming back. The engine does.</summary>
    [Fact]
    public async Task One_send_at_a_time_a_second_while_the_first_runs_is_refused()
    {
        var client = await StartWithResolveAsync();
        var project = await ClipsApprovedAsync(client);
        await AssemblyAsync(client, project.Id);
        var gate = new TaskCompletionSource();
        _resolve.Gate = gate.Task;

        var first = client.SendToResolveAsync(project.Id, 1, "Dark Eye");
        for (var waited = 0; _resolve.Sends.Count == 0 && waited < 500; waited++)
        {
            await Task.Delay(10);   // until the first is with Resolve
        }
        var second = await client.SendToResolveAsync(project.Id, 1, "Dark Eye");
        gate.SetResult();

        Assert.Equal((false, "busy"), (second.Ok, second.Stage));
        Assert.Contains("still sending", second.Problem);
        Assert.True((await first).Ok);
        Assert.Single(_resolve.Sends);
        _resolve.Gate = null;
        Assert.True((await client.SendToResolveAsync(project.Id, 1, "Dark Eye 2")).Ok);   // free again after
    }

    [Fact]
    public async Task Send_to_Resolve_needs_a_name_and_a_version_that_exists()
    {
        var client = await StartWithResolveAsync();
        var project = await ClipsApprovedAsync(client);
        await AssemblyAsync(client, project.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendToResolveAsync(project.Id, 1, "   "));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.SendToResolveAsync(project.Id, 9, "Dark Eye"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.SendToResolveAsync(Guid.NewGuid(), 1, "Dark Eye"));
        Assert.Empty(_resolve.Sends);
    }

    internal sealed class FakeResolveSenders : IResolveSenders
    {
        public List<(ResolveInstall Install, SendToResolveRequest Request)> Sends { get; } = [];

        public SendToResolveResult Answer { get; set; } = new(true, "done", null, "timeline", 1, 0);

        /// <summary>While set, a send waits for it.</summary>
        public Task? Gate { get; set; }

        public IResolveSender For(ResolveInstall install) => new Sender(this, install);

        private sealed class Sender(FakeResolveSenders owner, ResolveInstall install) : IResolveSender
        {
            public async Task<SendToResolveResult> SendAsync(SendToResolveRequest request, CancellationToken cancellationToken)
            {
                owner.Sends.Add((install, request));
                if (owner.Gate is { } gate)
                {
                    await gate;
                }
                return owner.Answer;
            }
        }
    }
}
