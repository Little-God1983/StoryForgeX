using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StoryForge.App.Interop;

/// <summary>
/// Gives the native title bar the dark look of the app, so a dark window does not sit under a
/// white strip. Cosmetic only: on a Windows build that ignores an attribute nothing changes.
/// </summary>
internal static partial class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;   // DWMWA_USE_IMMERSIVE_DARK_MODE, Windows 10 20H1+
    private const int CaptionColor = 35;           // DWMWA_CAPTION_COLOR, Windows 11

    // The top bar's colour (#0F1012) as a COLORREF, which is 0x00BBGGRR.
    private const int TopBarColorRef = 0x0012100F;

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var enabled = 1;
        DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int));
        var color = TopBarColorRef;
        DwmSetWindowAttribute(handle, CaptionColor, ref color, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
