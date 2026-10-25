using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StoryForge.Client;
using StoryForge.Engine.Research;

namespace StoryForge.Engine.References;

/// <summary>What Claude is asked to look at: one source picture of a reference.</summary>
/// <param name="Model">The model to use; empty means the provider's default.</param>
internal sealed record PictureCheckRequest(Guid ProjectId, string Name, CastKind Kind, string KeyReason, string PicturePath, string Model);

/// <summary>What Claude saw.</summary>
/// <param name="Usable">The picture really shows the reference.</param>
/// <param name="Complete">All of it is in the picture: the whole figure, place or object.</param>
/// <param name="Missing">What is not in it, e.g. "legs, shoes"; empty when complete.</param>
/// <param name="Look">What it looks like, in words, for every prompt that shows it.</param>
internal sealed record PictureVerdict(bool Usable, bool Complete, string Missing, string Look);

/// <summary>Looks at a source picture before a reference is made from it.</summary>
internal interface IPictureCheck
{
    /// <exception cref="Pipeline.StageFailedException">The model could not be asked.</exception>
    Task<PictureVerdict> CheckAsync(PictureCheckRequest request, IProgress<ActivityLine> activity, CancellationToken cancellationToken);
}

// Plain ASCII: npm's claude.cmd passes the schema through cmd.exe.
internal sealed record PictureCheckOutput(
    [property: Description("True when the picture really shows the one named, false when it shows something else.")] bool Usable,
    [property: Description("True when all of it is in the picture, false when part of it is cut off or hidden.")] bool Complete,
    [property: Description("What is not in the picture, as a short list, e.g. legs, shoes. Empty when it is complete.")] string Missing,
    [property: Description("One or two sentences on how it looks: shape, colours, clothes, materials, features. No story, no names of places.")] string Look);

/// <summary>
/// Claude CLI looking at the picture with its Read tool, and nothing else: the picture is copied
/// into the call's own folder, the one place Read is allowed.
/// </summary>
internal sealed class ClaudeCliPictureCheck(ClaudeCli claude, IOptions<StoryForgeEngineOptions> options, TimeProvider clock) : IPictureCheck
{
    internal const string SystemPrompt =
        """
        You check source pictures for StoryForge X, which makes narrated videos. A reference image is
        made from each picture: a clean picture of one character, place or object that every later
        image is built from. You say whether the picture shows the one named, whether all of it is in
        the picture, and how it looks.
        """;

    internal static string Schema { get; } = OutputSchema.Json<PictureCheckOutput>();

    public async Task<PictureVerdict> CheckAsync(PictureCheckRequest request, IProgress<ActivityLine> activity, CancellationToken cancellationToken)
    {
        activity.Report(new ActivityLine(clock.GetUtcNow(), ActivityKind.Model, $"looking at the picture of {request.Name}"));
        var reply = await claude.AskAsync(
            Call(request, options.Value.DataDirectory),
            new ClaudeStream(new ResearchPages(), activity, clock, $"looking at {request.Name}"),
            cancellationToken);
        if (reply.CostUsd is { } cost)
        {
            activity.Report(new ActivityLine(clock.GetUtcNow(), ActivityKind.Model, string.Create(CultureInfo.InvariantCulture, $"answer received · ${cost:0.00}")));
        }
        PictureCheckOutput? output;
        try
        {
            output = OutputSchema.Read<PictureCheckOutput>(reply.Output);
        }
        catch (JsonException)
        {
            output = null;
        }
        // No answer to read: the picture is taken as it is, and the card shows it to you all the same.
        return output is null
            ? new PictureVerdict(true, true, "", "")
            : new PictureVerdict(output.Usable, output.Complete, output.Complete ? "" : (output.Missing ?? "").Trim(), (output.Look ?? "").Trim());
    }

    /// <summary>The call: the picture copied into a folder of its own, which is all Read may reach.</summary>
    internal static ClaudeCall Call(PictureCheckRequest request, string dataDirectory)
    {
        var folder = Path.Combine(dataDirectory, "references", request.ProjectId.ToString("N"));
        Directory.CreateDirectory(folder);
        var name = "picture" + Path.GetExtension(request.PicturePath).ToLowerInvariant();
        File.Copy(request.PicturePath, Path.Combine(folder, name), overwrite: true);
        return new ClaudeCall(
            folder,
            SystemPrompt,
            Prompt(request.Name, request.Kind, request.KeyReason, "./" + name),
            Schema,
            request.Model,
            Session: null,
            McpConfig: null,
            AllowedTools: ["Read(./**)"],
            $"the picture of {request.Name}",
            BuiltInTools: ["Read"]);
    }

    internal static string Prompt(string name, CastKind kind, string keyReason, string picture)
    {
        var whole = kind switch
        {
            CastKind.Character => "the whole figure, head to feet",
            CastKind.Place => "the place as a whole, not a corner of it",
            _ => "the whole object",
        };
        return
            $"""
            Read the picture {picture} with the Read tool and look at it.

            It should show {name}, a {kind.ToString().ToLowerInvariant()}{(keyReason.Length > 0 ? $" ({keyReason})" : "")}.
            1. usable: does it really show {name}? A different character, a map or a screenshot of a menu is not usable.
            2. complete: is all of it in the picture, {whole}? Cut off or hidden parts make it incomplete.
            3. missing: what is not in the picture, e.g. legs, shoes. Leave it empty when it is complete.
            4. look: one or two sentences on how {name} looks, as the picture shows it.
            """;
    }
}
