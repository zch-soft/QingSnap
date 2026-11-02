using System.Windows.Automation;
using QingSnap.App.Infrastructure;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace QingSnap.App.Services;

internal readonly record struct UiAutomationNode(
    string ControlTypeName,
    string ClassName,
    WpfRect Bounds);

/// <summary>
/// Resolves docked tool window panes (e.g. Visual Studio's ViewPresenter /
/// ToolWindowTabGroupContainer) that have no HWND of their own. The pane rect
/// includes the title/tab strip, unlike the inner HwndHost content window.
/// </summary>
internal static class UiAutomationWindowProbe
{
    private const int MaxAncestorDepth = 16;

    private static readonly HashSet<string> DockedPaneClassNames = new(StringComparer.Ordinal)
    {
        "ViewPresenter",
        "ToolWindowTabGroupContainer"
    };

    internal static WpfRect? SelectDockedPane(IReadOnlyList<UiAutomationNode> chain)
    {
        WpfRect? match = null;
        foreach (var node in chain)
        {
            if (node.ControlTypeName == "ControlType.Window")
            {
                break;
            }

            if (node.ControlTypeName == "ControlType.Pane" &&
                DockedPaneClassNames.Contains(node.ClassName) &&
                !node.Bounds.IsEmpty &&
                node.Bounds.Width > 0 &&
                node.Bounds.Height > 0)
            {
                // Keep the outermost match so a pane group wins over its inner presenter.
                match = node.Bounds;
            }
        }

        return match;
    }

    internal static WpfRect? GetDockedPaneAtPoint(
        int screenX,
        int screenY,
        nint hitThroughWindow)
    {
        var previousStyle = hitThroughWindow == nint.Zero
            ? 0
            : NativeMethods.GetWindowLongPtr(hitThroughWindow, NativeMethods.GwlExStyle).ToInt64();
        var madeTransparent = hitThroughWindow != nint.Zero &&
                              (previousStyle & NativeMethods.WsExTransparent) == 0;
        AutomationElement? element;
        try
        {
            if (madeTransparent)
            {
                NativeMethods.SetWindowLongPtr(
                    hitThroughWindow,
                    NativeMethods.GwlExStyle,
                    (nint)(previousStyle | NativeMethods.WsExTransparent));
            }

            element = AutomationElement.FromPoint(new WpfPoint(screenX, screenY));
        }
        catch (Exception)
        {
            // UIA providers of third-party apps can fail or go away mid-query.
            return null;
        }
        finally
        {
            if (madeTransparent)
            {
                NativeMethods.SetWindowLongPtr(
                    hitThroughWindow,
                    NativeMethods.GwlExStyle,
                    (nint)previousStyle);
            }
        }

        if (element is null)
        {
            return null;
        }

        var chain = new List<UiAutomationNode>(MaxAncestorDepth);
        try
        {
            for (var depth = 0; depth < MaxAncestorDepth && element is not null; depth++)
            {
                var current = element.Current;
                chain.Add(new UiAutomationNode(
                    current.ControlType.ProgrammaticName,
                    current.ClassName,
                    current.BoundingRectangle));
                if (current.ControlType == ControlType.Window)
                {
                    break;
                }

                element = TreeWalker.ControlViewWalker.GetParent(element);
                if (element == AutomationElement.RootElement)
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        return SelectDockedPane(chain);
    }
}
