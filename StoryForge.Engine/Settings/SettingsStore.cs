using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;

namespace StoryForge.Engine.Settings;

/// <summary>
/// Reads and writes <see cref="EngineSettings"/>. Each group is its own row, so a group added
/// later starts from its defaults without touching what was saved before.
/// </summary>
internal sealed class SettingsStore(IDbContextFactory<StoryForgeDbContext> contextFactory)
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private const string ClaudeCliKey = "providers.claude-cli";
    private const string LmStudioKey = "providers.lm-studio";
    private const string ComfyUiKey = "providers.comfyui";
    private const string FfmpegKey = "providers.ffmpeg";
    private const string PathsKey = "paths";

    public async Task<EngineSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var saved = await db.Settings.AsNoTracking().ToDictionaryAsync(e => e.Key, e => e.Json, cancellationToken);
        var defaults = EngineSettings.Defaults;

        return new EngineSettings(
            Read(saved, ClaudeCliKey, defaults.ClaudeCli),
            Read(saved, LmStudioKey, defaults.LmStudio),
            Read(saved, ComfyUiKey, defaults.ComfyUi),
            Read(saved, FfmpegKey, defaults.Ffmpeg),
            Read(saved, PathsKey, defaults.Paths));
    }

    public async Task SaveAsync(EngineSettings settings, CancellationToken cancellationToken)
    {
        Validate(settings);
        settings = Normalize(settings);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Upsert(db, ClaudeCliKey, settings.ClaudeCli);
        Upsert(db, LmStudioKey, settings.LmStudio);
        Upsert(db, ComfyUiKey, settings.ComfyUi);
        Upsert(db, FfmpegKey, settings.Ffmpeg);
        Upsert(db, PathsKey, settings.Paths);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void Validate(EngineSettings settings)
    {
        Require(settings.ClaudeCli.TimeoutSeconds >= 1, "Claude CLI timeout must be at least 1 second.");
        Require(settings.ClaudeCli.MaxParallel >= 1, "Claude CLI max parallel must be at least 1.");
        Require(settings.ComfyUi.Port is >= 1 and <= 65535, "ComfyUI port must be between 1 and 65535.");
        Require(settings.ComfyUi.GpuSlots >= 1, "ComfyUI GPU slots must be at least 1.");
    }

    // Explorer's "Copy as path" wraps paths in quotes; a quoted path is never a file that exists.
    private static EngineSettings Normalize(EngineSettings s) => s with
    {
        ClaudeCli = s.ClaudeCli with { Executable = Unquote(s.ClaudeCli.Executable) },
        ComfyUi = s.ComfyUi with { WorkflowTemplatesFolder = Unquote(s.ComfyUi.WorkflowTemplatesFolder) },
        Ffmpeg = s.Ffmpeg with { Executable = Unquote(s.Ffmpeg.Executable) },
        Paths = s.Paths with { ProjectsFolder = Unquote(s.Paths.ProjectsFolder) },
    };

    private static string Unquote(string path)
    {
        var trimmed = path.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"' ? trimmed[1..^1].Trim() : trimmed;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ArgumentException(message);
        }
    }

    /// <summary>
    /// The saved group laid over its defaults field by field, so a field added in a later version
    /// gets its default rather than null. A row that can't be read at all (corrupt, or an enum
    /// value renamed since) falls back to the group's defaults instead of breaking startup.
    /// </summary>
    private static T Read<T>(IReadOnlyDictionary<string, string> saved, string key, T fallback)
    {
        if (!saved.TryGetValue(key, out var json))
        {
            return fallback;
        }
        try
        {
            var merged = JsonSerializer.SerializeToNode(fallback, Json)!.AsObject();
            if (JsonNode.Parse(json) is JsonObject stored)
            {
                foreach (var (name, value) in stored)
                {
                    if (value is not null)   // a stored null keeps the default
                    {
                        merged[name] = value.DeepClone();
                    }
                }
            }
            return merged.Deserialize<T>(Json) ?? fallback;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            return fallback;
        }
    }

    private static void Upsert<T>(StoryForgeDbContext db, string key, T value)
    {
        var json = JsonSerializer.Serialize(value, Json);
        var entry = db.Settings.Find(key);
        if (entry is null)
        {
            db.Settings.Add(new SettingsEntry { Key = key, Json = json });
        }
        else
        {
            entry.Json = json;
        }
    }
}
