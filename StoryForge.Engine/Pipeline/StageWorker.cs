using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using StoryForge.Client;

namespace StoryForge.Engine.Pipeline;

/// <summary>What a stage runs with: the project, and where it reports what it is doing.</summary>
internal sealed record StageContext(Project Project, IProgress<ActivityLine> Activity);

/// <summary>A stage's finished result, ready to store as a new version.</summary>
/// <param name="OutputJson">The result in <see cref="StoredJson"/> form.</param>
internal sealed record StageResult(string OutputJson, int SchemaVersion, string InputHash);

/// <summary>A stage that could not produce a result; the message is what the cell shows, in plain words.</summary>
internal sealed class StageFailedException(string reason) : Exception(reason);

/// <summary>The work of one stage. The runner handles queueing, states, versions and gates around it.</summary>
internal interface IStageWorker
{
    PipelineStage Stage { get; }

    /// <exception cref="StageFailedException">The stage failed; the reason is shown on its cell.</exception>
    Task<StageResult> RunAsync(StageContext context, CancellationToken cancellationToken);
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
