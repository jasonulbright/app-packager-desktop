using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ApplicationPackagerUI.Helpers;

/// <summary>
/// Closes the tooltips that show now. A modal window disables its owner, and a tooltip that the owner
/// showed under the mouse stays open on top of the new window until the owner is enabled again, so it
/// covers the buttons of the dialog.
/// </summary>
public static class OpenToolTips
{
    /// <summary>A tooltip popup holds the tooltip a few visuals below its root.</summary>
    private const int MaxDepth = 6;

    /// <summary>Closes every open tooltip of this thread and returns how many it closed.</summary>
    public static int Close()
    {
        var closed = 0;
        foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>().ToList())
        {
            if (source.IsDisposed || source.RootVisual is not { } root) continue;
            foreach (var tip in Find(root, 0))
            {
                if (!tip.IsOpen) continue;
                tip.IsOpen = false;
                closed++;
            }
        }
        return closed;
    }

    private static IEnumerable<ToolTip> Find(DependencyObject element, int depth)
    {
        if (element is ToolTip tip)
        {
            yield return tip;
            yield break;
        }
        if (depth >= MaxDepth || element is not (Visual or System.Windows.Media.Media3D.Visual3D)) yield break;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            foreach (var found in Find(VisualTreeHelper.GetChild(element, i), depth + 1))
                yield return found;
    }
}
