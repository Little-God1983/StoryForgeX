using System.Text.Json.Nodes;
using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.References;
using StoryForge.Engine.Voice;

namespace StoryForge.Engine.Tests;

/// <summary>Sources with pictures set up by name and by page; a picture listed as broken does not download.</summary>
internal sealed class FakePictures : IPictureSource
{
    public Dictionary<string, IReadOnlyList<FoundPicture>> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, IReadOnlyList<FoundPicture>> ByPage { get; } = [];

    public HashSet<string> Broken { get; } = [];

    public List<string> Searched { get; } = [];

    /// <summary>How long a search takes, so two can run at once.</summary>
    public TimeSpan Delay { get; set; }

    public async Task<IReadOnlyList<FoundPicture>> SearchAsync(IReadOnlyList<string> sources, string name, CancellationToken cancellationToken)
    {
        lock (Searched)
        {
            Searched.Add(name);
        }
        await Task.Delay(Delay, cancellationToken);
        return ByName.GetValueOrDefault(name) ?? [];
    }

    public Task<IReadOnlyList<FoundPicture>> PageAsync(IReadOnlyList<string> sources, string pageUrl, CancellationToken cancellationToken) =>
        Task.FromResult(ByPage.GetValueOrDefault(pageUrl) ?? []);

    /// <summary>What a download of the address gives; three bytes that are no picture by default.</summary>
    public Dictionary<string, byte[]> Content { get; } = [];

    public Task<DownloadedPicture?> DownloadAsync(string url, CancellationToken cancellationToken) =>
        Task.FromResult(Broken.Contains(url) ? null : new DownloadedPicture(Content.GetValueOrDefault(url) ?? [1, 2, 3], url.EndsWith(".jpg", StringComparison.Ordinal) ? ".jpg" : ".png"));

    public static FoundPicture Picture(string name, int width = 800, int height = 1200, bool main = true, string page = "https://bg3.wiki/wiki/Karlach") =>
        new($"https://bg3.wiki/w/images/{name}", page, Path.GetFileNameWithoutExtension(name), name, width, height, main);
}

/// <summary>Claude looking at pictures: by default every picture is usable and complete.</summary>
internal sealed class FakeCheck : IPictureCheck
{
    public Dictionary<string, PictureVerdict> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<PictureCheckRequest> Requests { get; } = [];

    /// <summary>Decides first, picture by picture; null leaves it to <see cref="ByName"/>.</summary>
    public Func<PictureCheckRequest, PictureVerdict?>? Decide { get; set; }

    public Task<PictureVerdict> CheckAsync(PictureCheckRequest request, IProgress<ActivityLine> activity, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }
        return Task.FromResult(Decide?.Invoke(request) ?? ByName.GetValueOrDefault(request.Name) ?? new PictureVerdict(true, true, "", $"{request.Name} as the picture shows."));
    }
}

/// <summary>ComfyUI making images: each run saves one PNG; a run can be made to fail.</summary>
internal sealed class FakeImageComfy : IComfyUi
{
    public List<JsonObject> Workflows { get; } = [];

    public List<string> Uploaded { get; } = [];

    public string? Failure { get; set; }

    /// <summary>Only runs whose prompt names this fail with <see cref="Failure"/>; null fails them all.</summary>
    public string? FailFor { get; set; }

    /// <summary>While set, every run waits for it: the stage stays running.</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Only runs whose prompt names this wait for <see cref="Hold"/>; null holds them all.</summary>
    public string? HoldFor { get; set; }

    /// <summary>The name of the picture each run saves.</summary>
    public string Saved { get; set; } = "ComfyUI_00001_.png";

    public Task<string> UploadAsync(string path, CancellationToken cancellationToken)
    {
        lock (Uploaded)
        {
            Uploaded.Add(Path.GetFileName(path));
        }
        return Task.FromResult("uploaded-" + Path.GetFileName(path));
    }

    public async Task<IReadOnlyList<ComfyFile>> RunAsync(JsonObject workflow, string name, IProgress<string> progress, CancellationToken cancellationToken)
    {
        if (Hold is { } hold && (HoldFor is null || workflow.ToJsonString().Contains(HoldFor, StringComparison.Ordinal)))
        {
            await hold.Task.WaitAsync(cancellationToken);
        }
        if (Failure is { } reason && (FailFor is null || workflow.ToJsonString().Contains(FailFor, StringComparison.Ordinal)))
        {
            throw new StageFailedException(reason);
        }
        lock (Workflows)
        {
            Workflows.Add(workflow);
        }
        // A video workflow saves a clip, as Video Combine does: with its sound, under its own name.
        return workflow.ToJsonString().Contains("VHS_VideoCombine", StringComparison.Ordinal)
            ? [new ComfyFile("gifs", "clip_00001.png", [137, 80, 78, 71]), new ComfyFile("gifs", "clip_00001-audio.mp4", [0, 0, 0, 24])]
            : [new ComfyFile("images", Saved, [137, 80, 78, 71])];
    }

    /// <summary>How often ComfyUI was asked to free its memory.</summary>
    public int Freed { get; private set; }

    /// <summary>While set, freeing the memory fails with it.</summary>
    public string? FreeFailure { get; set; }

    public Task FreeMemoryAsync(CancellationToken cancellationToken)
    {
        Freed++;
        return FreeFailure is { } reason ? Task.FromException(new StageFailedException(reason)) : Task.CompletedTask;
    }

    /// <summary>An API-format image workflow: a picture in, a prompt, a seed, a size, a picture out.</summary>
    public const string Workflow = """
        {
          "1": { "class_type": "LoadImage", "inputs": { "image": "" } },
          "2": { "class_type": "TextEncodeQwenImage21", "inputs": { "prompt": "" } },
          "3": { "class_type": "KSampler", "inputs": { "seed": 1 } },
          "4": { "class_type": "EmptyLatentImage", "inputs": { "width": 512, "height": 512 } },
          "5": { "class_type": "SaveImage", "inputs": { "images": ["3", 0] } }
        }
        """;

    public static IReadOnlyList<WorkflowInput> Inputs { get; } =
        [new("prompt", "#2.prompt"), new("image", "#1.image"), new("seed", "#3.seed"), new("width", "#4.width"), new("height", "#4.height")];
}
