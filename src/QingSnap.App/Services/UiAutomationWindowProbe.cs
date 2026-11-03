using System.Windows.Automation;
using WpfRect = System.Windows.Rect;

namespace QingSnap.App.Services;

internal readonly record struct PreviewOverride(WpfRect Bounds, bool IsDockPane);

/// <summary>
/// Resolves UIA elements that have no HWND of their own: toolbars, dock window
/// panes (a Pane with a title/tab child) and large content sections inside them.
/// The rules only use UIA control types, geometry and element hierarchy.
/// </summary>
internal static class UiAutomationWindowProbe
{
    private const int MaxDepth = 22;
    private const int MaxVisitedElements = 400;
    private const long QueryThrottleMilliseconds = 400;
    private const double SectionAreaRatio = 0.35;
    private const double SectionMaxAreaRatio = 0.90;
    private const double ToolbarMaxHeightRatio = 0.08;
    private const double ToolbarMinWidthRatio = 0.70;
    private const double ToolbarTopRatio = 0.08;
    private static long _lastQueryTick;

    internal static PreviewOverride? GetPreviewOverrideAtPoint(
        nint topLevelWindow,
        int screenX,
        int screenY,
        bool allowDockPane)
    {
        if (topLevelWindow == nint.Zero)
        {
            return null;
        }

        var now = Environment.TickCount64;
        if (now - _lastQueryTick < QueryThrottleMilliseconds)
        {
            return null;
        }

        _lastQueryTick = now;
        return FindPreviewOverrideInWindow(topLevelWindow, screenX, screenY, allowDockPane);
    }

    private static PreviewOverride? FindPreviewOverrideInWindow(
        nint topLevelWindow,
        double x,
        double y,
        bool allowDockPane)
    {
        AutomationElement root;
        try
        {
            root = AutomationElement.FromHandle(topLevelWindow);
        }
        catch (Exception)
        {
            return null;
        }

        var paneElement = FindPaneContainer(root, x, y, 0, new Counter());
        var scanRoot = paneElement ?? root;
        WpfRect paneBounds;
        try
        {
            paneBounds = scanRoot.Current.BoundingRectangle;
        }
        catch (Exception)
        {
            return null;
        }

        WpfRect? toolbarByType = null;
        WpfRect? toolbarByShape = null;
        WpfRect? section = null;
        ScanAll(scanRoot, 0, new Counter());

        if (toolbarByType is { } typeToolbar)
        {
            return new PreviewOverride(typeToolbar, IsDockPane: false);
        }

        if (toolbarByShape is { } shapedToolbar)
        {
            return new PreviewOverride(shapedToolbar, IsDockPane: false);
        }

        if (section is { } sectionBounds)
        {
            return new PreviewOverride(sectionBounds, IsDockPane: false);
        }

        return allowDockPane && paneElement is not null
            ? new PreviewOverride(paneBounds, IsDockPane: true)
            : null;

        AutomationElement? FindPaneContainer(
            AutomationElement element,
            double pointX,
            double pointY,
            int depth,
            Counter counter)
        {
            if (depth > MaxDepth || counter.Value >= MaxVisitedElements)
            {
                return null;
            }

            counter.Value++;
            AutomationElement.AutomationElementInformation current;
            try
            {
                current = element.Current;
            }
            catch (Exception)
            {
                return null;
            }

            var bounds = current.BoundingRectangle;
            if (!bounds.Contains(pointX, pointY))
            {
                return null;
            }

            if (current.ControlType == ControlType.Pane &&
                HasArea(bounds) &&
                HasTitleOrTabChild(element))
            {
                return element;
            }

            AutomationElement? child;
            try
            {
                child = TreeWalker.ControlViewWalker.GetFirstChild(element);
            }
            catch (Exception)
            {
                return null;
            }

            while (child is not null)
            {
                var found = FindPaneContainer(child, pointX, pointY, depth + 1, counter);
                if (found is not null)
                {
                    return found;
                }

                try
                {
                    child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                }
                catch (Exception)
                {
                    break;
                }
            }

            return null;
        }

        void ScanAll(AutomationElement element, int depth, Counter counter)
        {
            if (depth > MaxDepth || counter.Value >= MaxVisitedElements)
            {
                return;
            }

            counter.Value++;
            AutomationElement.AutomationElementInformation current;
            try
            {
                current = element.Current;
            }
            catch (Exception)
            {
                return;
            }

            var bounds = current.BoundingRectangle;
            if (bounds.Contains(x, y))
            {
                var controlType = current.ControlType;
                if (toolbarByType is null && controlType == ControlType.ToolBar && HasArea(bounds))
                {
                    toolbarByType = bounds;
                }
                else if (IsToolbarShape(controlType, bounds, paneBounds) &&
                         (toolbarByShape is null || Area(toolbarByShape.Value) <= Area(bounds)))
                {
                    // Keep the deepest, narrowest top strip: the toolbar row rather
                    // than its surrounding header block.
                    toolbarByShape = bounds;
                }

                if (controlType != ControlType.TitleBar &&
                    controlType != ControlType.TabItem &&
                    IsSectionShape(bounds, paneBounds))
                {
                    // Deeper large elements are the content section (swimlanes,
                    // summary tabs, a tree view, ...).
                    section = bounds;
                }
            }

            // The UIA parent chain can report stale rectangles (VS reports some tab
            // children far outside their parent), so do not prune by ancestor bounds.
            AutomationElement? child;
            try
            {
                child = TreeWalker.RawViewWalker.GetFirstChild(element);
            }
            catch (Exception)
            {
                return;
            }

            while (child is not null)
            {
                ScanAll(child, depth + 1, counter);
                try
                {
                    child = TreeWalker.RawViewWalker.GetNextSibling(child);
                }
                catch (Exception)
                {
                    break;
                }
            }
        }
    }

    private sealed class Counter
    {
        internal int Value;
    }

    internal static bool IsToolbarShape(ControlType controlType, WpfRect bounds, WpfRect pane)
    {
        if (controlType == ControlType.Text ||
            controlType == ControlType.Button ||
            controlType == ControlType.TitleBar ||
            controlType == ControlType.TabItem ||
            controlType == ControlType.Tab)
        {
            return false;
        }

        return pane.Width > 0 &&
               pane.Height > 0 &&
               bounds.Width >= pane.Width * ToolbarMinWidthRatio &&
               bounds.Height <= pane.Height * ToolbarMaxHeightRatio &&
               bounds.Top <= pane.Top + pane.Height * ToolbarTopRatio;
    }

    internal static bool IsSectionShape(WpfRect bounds, WpfRect pane)
    {
        var paneArea = Area(pane);
        if (paneArea <= 0)
        {
            return false;
        }

        var ratio = Area(bounds) / paneArea;
        return ratio >= SectionAreaRatio && ratio <= SectionMaxAreaRatio;
    }

    private static bool HasTitleOrTabChild(AutomationElement element)
    {
        AutomationElement? child;
        try
        {
            child = TreeWalker.ControlViewWalker.GetFirstChild(element);
        }
        catch (Exception)
        {
            return false;
        }

        while (child is not null)
        {
            try
            {
                var controlType = child.Current.ControlType;
                if (controlType == ControlType.TitleBar || controlType == ControlType.Tab)
                {
                    return true;
                }

                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }

    private static double Area(WpfRect bounds)
    {
        return bounds.Width * bounds.Height;
    }

    private static bool HasArea(WpfRect bounds)
    {
        return !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0;
    }
}
