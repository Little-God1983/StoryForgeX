using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using StoryForge.Client;

namespace StoryForge.Engine.Pipeline;

/// <summary>What a stage runs with: the project, and where it reports what it is doing.</summary>
/// <param name="StoreSegment">
/// Stores one segment's result at once, while the stage goes on with the next: a slow stage (the
/// voice) shows each segment as soon as it is done. Its result's own segments then skip the ones
/// stored this way. Null outside a whole-stage run.
/// </param>
internal sealed record StageContext(Project Project, IProgress<ActivityLine> Activity, Func<CellResult, Task>? StoreSegment = null);

/// <summary>A stage's finished result, ready to store as a new version.</summary>
/// <param name="OutputJson">The result in <see cref="StoredJson"/> form.</param>
/// <param name="Segments">For a stage with a result per segment (the script), each segment's own result.</param>
internal sealed record StageResult(string OutputJson, int SchemaVersion, string InputHash, IReadOnlyList<CellResult>? Segments = null);

/// <summary>One segment's result: stored as the next version of the segment's own cell.</summary>
internal sealed record CellResult(string Key, string OutputJson, int SchemaVersion, string InputHash);

/// <summary>A stage that could not produce a result; the message is what the cell shows, in plain words.</summary>
internal sealed class StageFailedException(string reason) : Exception(reason);

/// <summary>The work of one stage. The runner handles queueing, states, versions and gates around it.</summary>
internal interface IStageWorker
{
    PipelineStage Stage { get; }

    /// <exception cref="StageFailedException">The stage failed; the reason is shown on its cell.</exception>
    Task<StageResult> RunAsync(StageContext context, CancellationToken cancellationToken);
}

/// <summary>A stage whose segments can each be written again on their own (Regenerate on one segment).</summary>
internal interface ISegmentWorker : IStageWorker
{
    /// <exception cref="StageFailedException">The segment could not be written; the reason is shown on its cell.</exception>
    Task<CellResult> RunSegmentAsync(StageContext context, string key, CancellationToken cancellationToken);
}

/// <summary>How stage results and activity are stored: camelCase JSON, enums by name.</summary>
internal static class StoredJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Stored {typeof(T).Name} is empty.");
}

/// <summary>The input hash of a cell version: the same inputs always give the same hash.</summary>
internal static class InputHash
{
    public static string Of(object inputs) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(inputs, StoredJson.Options)));
}
