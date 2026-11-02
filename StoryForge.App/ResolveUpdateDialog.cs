using System.Diagnostics;
using System.Windows.Automation;

namespace StoryForge.App;

/// <summary>
/// Finds DaVinci Resolve's Software Update window and closes it (taken over from RawCutX for Send to Resolve, #71). While that window is up, the
/// scripting API's CreateProject refuses every name, so a send that cold-starts Resolve used to fail
/// with a name clash that was not there (2026-10-28).
///
/// <para>Matched by Qt class name (<c>UiAutoUpdateDialogImp</c>) on a top-level window of a
/// <c>Resolve</c> process, not by the "Software Update" title, which follows Resolve's UI
/// language. Closed through UI Automation's WindowPattern — what the window's own close button
/// does — never through its Skip button, which may skip the version for good. Checked against a
/// live 21.1.1: the window went, the Project Manager stayed, and no preference file was written.</para>
/// </summary>
public static class ResolveUpdateDialog
{
    private const string ClassName = "UiAutoUpdateDialogImp";

    /// <summary>Closes every such window and returns how many it closed; 0 when Resolve is not
    /// running or shows none. Called off the UI thread, every quarter second, for the length of a
    /// send — see the engine's <c>ResolveSender</c>.</summary>
    public static int CloseAll()
    {
        var closed = 0;
        foreach (var resolve in Process.GetProcessesByName("Resolve"))
        {
            using (resolve)
            {
                var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ProcessIdProperty, resolve.Id),
                        new PropertyCondition(AutomationElement.ClassNameProperty, ClassName)));

                foreach (AutomationElement window in windows)
                {
                    if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
                    {
                        ((WindowPattern)pattern).Close();
                        closed++;
                    }
                }
            }
        }

        return closed;
    }
}
