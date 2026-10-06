using System.Text.Json;
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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ArgumentException(message);
        }
    }

    private static T Read<T>(IReadOnlyDictionary<string, string> saved, string key, T fallback) =>
        saved.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json, Json) ?? fallback : fallback;

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
