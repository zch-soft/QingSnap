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
/// Resolves UIA elements that have no HWND of their own, such as Visual
/// Studio's docked tool window panes (ViewPresenter / ToolWindowTabGroupContainer)
/// and toolbars. Pane rects include the title/tab strip, unlike the inner
/// HwndHost content window.
/// </summary>
internal static class UiAutomationWindowProbe
{
    private const int MaxAncestorDepth = 16;
    private const long FallbackThrottleMilliseconds = 400;
    private static long _lastFallbackTick;

    private static readonly HashSet<string> DockedPaneClassNames = new(StringComparer.Ordinal)
    {
        "ViewPresenter",
        "ToolWindowTabGroupContainer"
    };

    internal static WpfRect? SelectTarget(IReadOnlyList<UiAutomationNode> chain)
    {
        // Toolbars sit inside panes; prefer the toolbar when the pointer is over one.
        return SelectToolbar(chain) ?? SelectDockedPane(chain);
    }

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

    internal static WpfRect? SelectToolbar(IReadOnlyList<UiAutomationNode> chain)
    {
        foreach (var node in chain)
        {
            if (node.ControlTypeName == "ControlType.Window")
            {
                break;
            }

            if (node.ControlTypeName == "ControlType.ToolBar" &&
                !node.Bounds.IsEmpty &&
                node.Bounds.Width > 0 &&
                node.Bounds.Height > 0)
            {
                return node.Bounds;
            }
        }

        return null;
    }

    internal static WpfRect? GetPreviewOverrideAtPoint(
        int screenX,
        int screenY,
        nint hitThroughWindow,
        nint fallbackTopLevelWindow)
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

        var target = SelectTarget(chain);
        if (target is not null)
        {
            return target;
        }

        // In a debug session the hit test can stop at the host window itself, so
        // descend the real window's tree by point to recover the pane/toolbar.
        if (fallbackTopLevelWindow != nint.Zero &&
            Environment.TickCount64 - _lastFallbackTick >= FallbackThrottleMilliseconds)
        {
            _lastFallbackTick = Environment.TickCount64;
            return FindPreviewOverrideInWindow(fallbackTopLevelWindow, screenX, screenY);
        }

        return null;
    }

    private static WpfRect? FindPreviewOverrideInWindow(nint topLevelWindow, double x, double y)
    {
        AutomationElement? element;
        try
        {
            element = AutomationElement.FromHandle(topLevelWindow);
        }
        catch (Exception)
        {
            return null;
        }

        WpfRect? pane = null;
        WpfRect? toolbar = null;
        try
        {
            for (var depth = 0; depth < MaxAncestorDepth && element is not null; depth++)
            {
                var current = element.Current;
                var bounds = current.BoundingRectangle;
                if (bounds.IsEmpty || !bounds.Contains(x, y))
                {
                    break;
                }

                if (toolbar is null &&
                    current.ControlType.ProgrammaticName == "ControlType.ToolBar" &&
                    bounds.Width > 0 &&
                    bounds.Height > 0)
                {
                    toolbar = bounds;
                }

                if (pane is null &&
                    IsDockedPane(current.ControlType.ProgrammaticName, current.ClassName) &&
                    bounds.Width > 0 &&
                    bounds.Height > 0)
                {
                    pane = bounds;
                }

                element = FindContainingChild(element, x, y);
            }
        }
        catch (Exception)
        {
            return toolbar ?? pane;
        }

        return toolbar ?? pane;
    }

    private static AutomationElement? FindContainingChild(
        AutomationElement parent,
        double x,
        double y)
    {
        AutomationElement? child;
        try
        {
            child = TreeWalker.ControlViewWalker.GetFirstChild(parent);
        }
        catch (Exception)
        {
            return null;
        }

        while (child is not null)
        {
            try
            {
                var bounds = child.Current.BoundingRectangle;
                if (!bounds.IsEmpty && bounds.Contains(x, y))
                {
                    return child;
                }

                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }

    private static bool IsDockedPane(string controlTypeName, string className)
    {
        return controlTypeName == "ControlType.Pane" &&
               DockedPaneClassNames.Contains(className);
    }
}
