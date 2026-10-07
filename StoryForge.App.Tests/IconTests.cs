using System.Collections;
using System.IO;
using System.Resources;
using System.Runtime.InteropServices;
using StoryForge.App.ViewModels;

namespace StoryForge.App.Tests;

/// <summary>
/// The StoryForge icon reaches both places Windows looks: the exe (Explorer, the Start Menu
/// shortcut) and the window (title bar, taskbar, Alt+Tab). Neither fails loudly when missing:
/// the exe just shows the blank default and the window the exe's.
/// </summary>
public sealed class IconTests
{
    [Fact]
    public void The_exe_carries_an_icon()
    {
        // The test output holds a copy of the app's exe. The .NET apphost has no icon of its own,
        // so any icon in it is the one ApplicationIcon put there.
        var exe = Path.Combine(AppContext.BaseDirectory, "StoryForge.App.exe");
        Assert.True(File.Exists(exe), exe);

        Assert.True(ExtractIconEx(exe, -1, IntPtr.Zero, IntPtr.Zero, 0) > 0);
    }

    [Fact]
    public void The_window_icon_is_compiled_into_the_app()
    {
        // MainWindow.xaml names /Assets/StoryForge.ico; a missing resource only shows when the
        // window opens, as a XamlParseException.
        var assembly = typeof(MainViewModel).Assembly;
        using var stream = assembly.GetManifestResourceStream("StoryForge.App.g.resources")!;
        using var reader = new ResourceReader(stream);

        var names = reader.Cast<DictionaryEntry>().Select(entry => (string)entry.Key);

        Assert.Contains("assets/storyforge.ico", names);
    }

    [Fact]
    public void Every_size_below_256_is_a_bitmap()
    {
        // Windows reads a PNG frame reliably only at 256 px. With PNG frames at the small sizes the
        // window and Explorer showed the icon, and the taskbar a blank tile.
        var repoRoot = Path.GetDirectoryName(BuildScriptTests.Pwsh.ScriptsFolder)!;
        var icon = File.ReadAllBytes(Path.Combine(repoRoot, "StoryForge.App", "Assets", "StoryForge.ico"));
        var count = BitConverter.ToUInt16(icon, 4);

        var pngBelow256 = Enumerable.Range(0, count)
            .Select(i => 6 + (16 * i))
            .Where(entry => icon[entry] != 0)   // 0 means 256
            .Where(entry => BitConverter.ToUInt32(icon, BitConverter.ToInt32(icon, entry + 12)) == 0x474E5089)   // "\x89PNG"
            .Select(entry => $"{icon[entry]} px");

        Assert.Empty(pngBelow256);
    }

    // With index -1 and no buffers it returns how many icons the file holds.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr large, IntPtr small, uint count);
}
