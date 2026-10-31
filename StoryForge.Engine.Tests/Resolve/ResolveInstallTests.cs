// Taken over from RawCutX's tests with its Send to Resolve (#71).
using StoryForge.Engine.Resolve;

namespace StoryForge.Engine.Tests.Resolve;

/// <summary>
/// Where Resolve and its bundled Python live. Settings-file overrides exist because an unusual
/// install can be repaired without a new build — the same escape hatch BreathModelDirectory gives
/// the breath model.
/// </summary>
public sealed class ResolveInstallTests
{
    [Fact]
    public void WithNoOverrides_ItUsesTheDefaultInstallPaths()
    {
        var install = ResolveInstall.Locate(null, null);

        Assert.Equal(ResolveInstall.DefaultResolveExe, install.ResolveExe);
        Assert.Equal(ResolveInstall.DefaultResolvePython, install.ResolvePython);
    }

    [Fact]
    public void AnOverrideWins()
    {
        var install = ResolveInstall.Locate(@"D:\Resolve\Resolve.exe", @"D:\Resolve\py.exe");

        Assert.Equal(@"D:\Resolve\Resolve.exe", install.ResolveExe);
        Assert.Equal(@"D:\Resolve\py.exe", install.ResolvePython);
    }

    [Fact]
    public void AnEmptyOverrideIsTreatedAsAbsent()
    {
        // A settings.json with "resolveExePath": "" means the user cleared the box, not that they
        // want to run a program with no name.
        var install = ResolveInstall.Locate("   ", "");

        Assert.Equal(ResolveInstall.DefaultResolveExe, install.ResolveExe);
        Assert.Equal(ResolveInstall.DefaultResolvePython, install.ResolvePython);
    }

    [Fact]
    public void BothPresent_IsNoProblem()
    {
        var install = ResolveInstall.Locate(null, null);

        Assert.Null(install.Problem(_ => true));
    }

    [Fact]
    public void AMissingInterpreter_NamesThePathAndWhereToSetIt()
    {
        var install = ResolveInstall.Locate(null, null);

        var problem = install.Problem(path => !path.Contains("ResolvePython", StringComparison.Ordinal));

        Assert.NotNull(problem);
        Assert.Contains(ResolveInstall.DefaultResolvePython, problem!, StringComparison.Ordinal);
        Assert.Contains("Settings → Resolve", problem, StringComparison.Ordinal);
        Assert.Contains("21.1", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingResolveExe_NamesThePathAndWhereToSetIt()
    {
        var install = ResolveInstall.Locate(null, null);

        var problem = install.Problem(path => path.Contains("ResolvePython", StringComparison.Ordinal));

        Assert.NotNull(problem);
        Assert.Contains("Settings → Resolve", problem!, StringComparison.Ordinal);
    }
}
