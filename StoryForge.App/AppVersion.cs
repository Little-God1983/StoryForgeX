using System.Reflection;

namespace StoryForge.App;

/// <summary>
/// The version this build calls itself, as the window shows it. Read from the informational version:
/// that carries what &lt;Version&gt; in Directory.Build.props or build.ps1's -p:Version= said, including
/// a one-off build's "-oneoff.g&lt;commit&gt;". Public because the window reaches it through x:Static.
/// </summary>
public static class AppVersion
{
    private const string AppName = "StoryForge X";

    // typeof rather than Assembly.GetEntryAssembly(): under a test runner the entry assembly is the test host.
    public static string Number { get; } = Display(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string WindowTitle => $"{AppName} {Number}".TrimEnd();

    // The SDK appends "+<commit>"; the window shows the number, not the hash.
    public static string Display(string? informationalVersion) => informationalVersion?.Split('+')[0] ?? "";
}
