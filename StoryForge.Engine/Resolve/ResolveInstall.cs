namespace StoryForge.Engine.Resolve;

/// <summary>
/// Where DaVinci Resolve and the Python interpreter it ships are on this machine (taken over from
/// RawCutX). The interpreter matters more than the application: from Resolve 21.1 it ships a Python
/// in which <c>import DaVinciResolveScript</c> resolves with no environment variables set, so StoryForge X
/// drives Resolve with no Python of its own.
/// </summary>
internal sealed record ResolveInstall(string ResolveExe, string ResolvePython)
{
    public const string DefaultResolveExe = @"C:\Program Files\Blackmagic Design\DaVinci Resolve\Resolve.exe";

    /// <summary>Resolve 21.1 and above; earlier versions ship no interpreter, which is why <see cref="Problem"/> names the version.</summary>
    public const string DefaultResolvePython = @"C:\Program Files\Blackmagic Design\DaVinci Resolve\ResolvePython\ResolvePython.exe";

    /// <summary>The install to use; an empty path in Settings → Resolve means the standard one.</summary>
    public static ResolveInstall Locate(string? resolveExe, string? resolvePython) =>
        new(Or(resolveExe, DefaultResolveExe), Or(resolvePython, DefaultResolvePython));

    private static string Or(string? candidate, string fallback) => string.IsNullOrWhiteSpace(candidate) ? fallback : candidate.Trim();

    /// <summary>What is missing, as the user reads it; null when both files are there.</summary>
    public string? Problem(Func<string, bool> exists)
    {
        if (!exists(ResolvePython))
        {
            return $"DaVinci Resolve's Python was not found at {ResolvePython}. Sending to Resolve needs DaVinci Resolve Studio 21.1 or newer, "
                + "the version that ships it. Set its path in Settings → Resolve if yours is installed elsewhere.";
        }
        if (!exists(ResolveExe))
        {
            return $"DaVinci Resolve was not found at {ResolveExe}. Set its path in Settings → Resolve.";
        }
        return null;
    }
}
