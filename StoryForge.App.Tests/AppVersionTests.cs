namespace StoryForge.App.Tests;

/// <summary>
/// The version number in the window's title bar and top bar: the one way to tell on screen which
/// build is running. Deliberately not compared with Directory.Build.props: scripts\build.ps1 builds
/// with -p:Version=&lt;next&gt; before it writes that number back, so the two legitimately disagree
/// during every stable build.
/// </summary>
public sealed class AppVersionTests
{
    [Fact]
    public void The_commit_hash_is_cut_off_the_number()
    {
        // The SDK appends "+<commit>" to the informational version.
        Assert.Equal("0.1.0.3", AppVersion.Display("0.1.0.3+d58daa121272300cf1a6273a07173978323778ca"));
    }

    [Theory]
    [InlineData("0.1.0.3")]
    [InlineData("0.1.0.3-oneoff.gabc1234")]
    public void A_number_without_a_hash_comes_through_as_it_is(string version)
    {
        // The second is what build.ps1 -InstallDir stamps on a one-off build.
        Assert.Equal(version, AppVersion.Display(version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+d58daa1")]
    public void No_version_leaves_the_number_empty(string? version)
    {
        Assert.Equal("", AppVersion.Display(version));
    }

    [Fact]
    public void The_title_names_the_app_and_the_version_it_was_built_as()
    {
        // AssemblyVersion is a separate attribute from the informational version the title reads,
        // and always four parts, so only major.minor is compared.
        var built = typeof(AppVersion).Assembly.GetName().Version!;

        Assert.StartsWith($"StoryForge X {built.Major}.{built.Minor}", AppVersion.WindowTitle);
        Assert.DoesNotContain("+", AppVersion.WindowTitle);
    }
}
