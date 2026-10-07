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

    // With index -1 and no buffers it returns how many icons the file holds.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr large, IntPtr small, uint count);
}
