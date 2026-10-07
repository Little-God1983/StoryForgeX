using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Settings;

namespace StoryForge.Engine.Profiles;

/// <summary>
/// Profiles and their versions. A save always adds a version; nothing ever changes a saved one,
/// so a project that used v2 can still see exactly what v2 was.
/// </summary>
internal sealed class ProfileStore(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    SettingsStore settingsStore,
    IOptions<StoryForgeEngineOptions> options,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new();

    private static readonly ProfileContent Empty = ProfileContent.Empty;

    // Numbering a version reads the latest and writes the next; one writer at a time keeps two
    // saves from both picking the same number.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task<IReadOnlyList<ProfileSummary>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var profiles = await db.Profiles.AsNoTracking()
            .Select(p => new ProfileSummary(p.Id, p.Kind, p.Name, p.Versions.Max(v => v.Version)))
            .ToListAsync(cancellationToken);
        return [.. profiles.OrderBy(p => p.Kind).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<ProfileSummary> CreateAsync(ProfileKind kind, string name, CancellationToken cancellationToken)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("A profile needs a name.");
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var taken = await db.Profiles.Where(p => p.Kind == kind).Select(p => p.Name).ToListAsync(cancellationToken);
            if (taken.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                // No parameter name: the message is shown on screen as it is.
                throw new ArgumentException($"Another {kind} profile is already called \"{name}\".");
            }

            var now = clock.GetUtcNow();
            var profile = new ProfileEntry { Id = Guid.NewGuid(), Kind = kind, Name = name, CreatedAt = now };
            profile.Versions.Add(new ProfileVersionEntry { Version = 1, SavedAt = now, Json = Serialize(Starter(kind)) });
            db.Profiles.Add(profile);
            await db.SaveChangesAsync(cancellationToken);
            return new ProfileSummary(profile.Id, kind, name, 1);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<ProfileVersion> GetVersionAsync(Guid profileId, int? version, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var versions = db.ProfileVersions.AsNoTracking().Where(v => v.ProfileId == profileId);
        var entry = version is { } number
            ? await versions.FirstOrDefaultAsync(v => v.Version == number, cancellationToken)
            : await versions.OrderByDescending(v => v.Version).FirstOrDefaultAsync(cancellationToken);
        return entry is null
            ? throw new KeyNotFoundException(version is null
                ? $"There is no profile {profileId}."
                : $"Profile {profileId} has no version {version}.")
            : ToVersion(entry);
    }

    public async Task<ProfileVersion> SaveVersionAsync(Guid profileId, ProfileContent content, CancellationToken cancellationToken)
    {
        Validate(content);
        var json = Serialize(Normalize(content));

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var latest = await db.ProfileVersions.AsNoTracking()
                .Where(v => v.ProfileId == profileId)
                .OrderByDescending(v => v.Version)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException($"There is no profile {profileId}.");

            // Compared as the latest would be saved today, so a field added since does not count as a change.
            if (Serialize(Read(latest.Json)) == json)
            {
                return ToVersion(latest);
            }

            var next = new ProfileVersionEntry
            {
                ProfileId = profileId,
                Version = latest.Version + 1,
                SavedAt = clock.GetUtcNow(),
                Json = json,
            };
            db.ProfileVersions.Add(next);
            await db.SaveChangesAsync(cancellationToken);
            return ToVersion(next);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Stored by content, so importing the same picture twice keeps one copy, and a profile never
    /// points at a file the user later moves or deletes.
    /// </summary>
    public Task<string> ImportReferenceFileAsync(string sourcePath, CancellationToken cancellationToken) =>
        // Off the caller's thread: the in-process client is called from the UI thread, and hashing
        // and copying a long WAV there would freeze the window.
        Task.Run(() => ImportAsync(sourcePath, cancellationToken), cancellationToken);

    private async Task<string> ImportAsync(string sourcePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"'{sourcePath}' does not exist.", sourcePath);
        }

        string hash;
        await using (var source = File.OpenRead(sourcePath))
        {
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, cancellationToken));
        }

        var folder = Path.Combine(options.Value.DataDirectory, "references");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, hash[..16] + Path.GetExtension(sourcePath).ToLowerInvariant());
        if (!File.Exists(target))
        {
            // Copied under a temporary name first, so a copy cut short never looks like the real file.
            var partial = target + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                File.Copy(sourcePath, partial);
                File.Move(partial, target);
            }
            catch (IOException) when (File.Exists(target))
            {
                // The same file imported at the same moment; that copy is as good as this one.
            }
            finally
            {
                // A copy cut short (disk full) or a move refused never leaves its half behind.
                File.Delete(partial);
            }
        }
        return target;
    }

    public async Task<IReadOnlyList<string>> GetWorkflowTemplatesAsync(CancellationToken cancellationToken)
    {
        var folder = (await settingsStore.LoadAsync(cancellationToken)).ComfyUi.WorkflowTemplatesFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            return [];
        }
        // Off the caller's thread: a folder on an unreachable network share takes the SMB timeout
        // to answer, and the in-process client is called from the UI thread.
        return await Task.Run(() => ListTemplates(folder), cancellationToken);
    }

    private static IReadOnlyList<string> ListTemplates(string folder) =>
        !Directory.Exists(folder)
            ? []
            :
            [
                .. Directory.EnumerateFiles(folder, "*.json")
                    .Where(f => Path.GetExtension(f).Equals(".json", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .Order(StringComparer.OrdinalIgnoreCase),
            ];

    private static void Validate(ProfileContent content)
    {
        Require(content.Steps is null or >= 1, "Steps must be at least 1.");
        Require(content.MaxClipSeconds is null or >= 1, "Max clip length must be at least 1 second.");
        foreach (var size in content.Sizes ?? [])
        {
            Require(!string.IsNullOrWhiteSpace(size.Aspect), "Every size needs an aspect, e.g. 16:9.");
            Require(size.Width >= 1 && size.Height >= 1, $"The size for {size.Aspect} must be at least 1 × 1.");
        }
        var aspects = (content.Sizes ?? []).Select(s => s.Aspect?.Trim()).ToList();
        Require(aspects.Distinct(StringComparer.OrdinalIgnoreCase).Count() == aspects.Count, "Each aspect can have only one size.");
        var keys = (content.Inputs ?? []).Select(i => i.Key?.Trim()).ToList();
        Require(keys.All(k => !string.IsNullOrEmpty(k)), "Every workflow input needs a key.");
        Require(keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == keys.Count, "Each workflow input key can appear only once.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ArgumentException(message);
        }
    }

    private static ProfileContent Normalize(ProfileContent c) => c with
    {
        Instructions = c.Instructions ?? "",
        ResearchSources = [.. (c.ResearchSources ?? []).Select(s => s.Trim()).Where(s => s.Length > 0)],
        Provider = (c.Provider ?? "").Trim(),
        WorkflowTemplate = (c.WorkflowTemplate ?? "").Trim(),
        PromptTemplate = c.PromptTemplate ?? "",
        NegativePrompt = c.NegativePrompt ?? "",
        Inputs = [.. (c.Inputs ?? []).Select(i => new WorkflowInput(i.Key.Trim(), (i.Node ?? "").Trim()))],
        Sizes = [.. (c.Sizes ?? []).Select(s => s with { Aspect = s.Aspect.Trim() })],
        ReferenceFiles = [.. c.ReferenceFiles ?? []],
        Voice = (c.Voice ?? "").Trim(),
    };

    private static string Serialize(ProfileContent content) => JsonSerializer.Serialize(content, Json);

    /// <summary>The saved fields laid over <see cref="Empty"/>, so a field added later reads as empty, not null.</summary>
    private static ProfileContent Read(string json)
    {
        var merged = JsonSerializer.SerializeToNode(Empty, Json)!.AsObject();
        if (JsonNode.Parse(json) is JsonObject stored)
        {
            foreach (var (name, value) in stored)
            {
                if (value is not null)
                {
                    merged[name] = value.DeepClone();
                }
            }
        }
        return merged.Deserialize<ProfileContent>(Json) ?? Empty;
    }

    private static ProfileVersion ToVersion(ProfileVersionEntry entry) =>
        new(entry.ProfileId, entry.Version, entry.SavedAt, Read(entry.Json));

    /// <summary>What a new profile starts with: enough to show what goes where, nothing model-specific.</summary>
    internal static ProfileContent Starter(ProfileKind kind) => kind switch
    {
        ProfileKind.Research => Empty with
        {
            Provider = "Claude CLI",
            Instructions = "Collect the facts the script needs: names, dates, places and numbers. Give the source for every fact.",
        },
        ProfileKind.Script => Empty with
        {
            Provider = "Claude CLI",
            Instructions = "Write the narration for the brief. Short sentences, written to be heard, not read.",
        },
        ProfileKind.Storyboard => Empty with
        {
            Provider = "Claude CLI",
            Instructions = "Split the script into shots. For every shot give what is seen, the camera, and the line of narration it covers.",
        },
        ProfileKind.Image => Empty with
        {
            Provider = "ComfyUI",
            Instructions = "Write one image prompt per shot for this model: subject first, then setting, light and style. No text in the image.",
            PromptTemplate = "{shot.visual}",
            Inputs = MediaInputs,
            Sizes = [new("16:9", 1344, 768), new("9:16", 768, 1344)],
        },
        ProfileKind.Video => Empty with
        {
            Provider = "ComfyUI",
            Instructions = "Write one motion prompt per shot: what moves and how the camera moves. The still already sets the look.",
            PromptTemplate = "{shot.motion}",
            Inputs = MediaInputs,
            Sizes = [new("16:9", 1280, 720), new("9:16", 720, 1280)],
            MaxClipSeconds = 20,
        },
        ProfileKind.Voice => Empty with
        {
            Provider = "ComfyUI",
            Instructions = "Prepare the narration for speech: write numbers, units and abbreviations out in full.",
            PromptTemplate = "{shot.narration}",
            Inputs = [new("text", ""), new("seed", "")],
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static readonly IReadOnlyList<WorkflowInput> MediaInputs =
    [
        new("prompt", ""), new("negative", ""), new("width", ""), new("height", ""), new("seed", ""), new("steps", ""),
    ];
}
