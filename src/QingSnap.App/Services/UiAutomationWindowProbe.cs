using System.Windows.Automation;
using QingSnap.App.Infrastructure;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace QingSnap.App.Services;

internal readonly record struct UiAutomationNode(
    string ControlTypeName,
    string ClassName,
    WpfRect Bounds);

internal readonly record struct PreviewOverride(WpfRect Bounds, bool IsDockPane);

/// <summary>
/// Resolves UIA elements that have no HWND of their own, such as Visual
/// Studio's toolbars, docked tool window panes (ViewPresenter /
/// ToolWindowTabGroupContainer) and their content sections.
/// </summary>
internal static class UiAutomationWindowProbe
{
    private const int MaxAncestorDepth = 22;
    private const int MaxVisitedElements = 140;
    private const long FallbackThrottleMilliseconds = 400;
    private static long _lastFallbackTick;

    private static readonly HashSet<string> DockedPaneClassNames = new(StringComparer.Ordinal)
    {
        "ViewPresenter",
        "ToolWindowTabGroupContainer"
    };

    private static readonly HashSet<string> SectionClassNames = new(StringComparer.Ordinal)
    {
        "ScriptedControl",
        "TabControl",
        "TreeView"
    };

    internal static PreviewOverride? SelectTarget(
        IReadOnlyList<UiAutomationNode> chain,
        bool allowDockPane)
    {
        if (SelectToolbar(chain) is { } toolbar)
        {
            return new PreviewOverride(toolbar, IsDockPane: false);
        }

        if (SelectSection(chain) is { } section)
        {
            return new PreviewOverride(section, IsDockPane: false);
        }

        return allowDockPane && SelectDockedPane(chain) is { } pane
            ? new PreviewOverride(pane, IsDockPane: true)
            : null;
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

            if (IsDockedPane(node.ControlTypeName, node.ClassName) && HasArea(node.Bounds))
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

            if (IsToolbar(node.ControlTypeName, node.ClassName) && HasArea(node.Bounds))
            {
                return node.Bounds;
            }
        }

        return null;
    }

    internal static WpfRect? SelectSection(IReadOnlyList<UiAutomationNode> chain)
    {
        foreach (var node in chain)
        {
            if (node.ControlTypeName == "ControlType.Window")
            {
                break;
            }

            if (IsSection(node.ControlTypeName, node.ClassName) && HasArea(node.Bounds))
            {
                return node.Bounds;
            }
        }

        return null;
    }

    internal static PreviewOverride? GetPreviewOverrideAtPoint(
        int screenX,
        int screenY,
        nint hitThroughWindow,
        nint fallbackTopLevelWindow,
        bool allowDockPane)
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

        if (element is not null)
        {
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
                chain.Clear();
            }

            if (SelectTarget(chain, allowDockPane) is { } target)
            {
                return target;
            }
        }

        // In a debug session the hit test can stop at the host window itself, so
        // descend the real window's tree by point to recover the pane/section.
        if (fallbackTopLevelWindow != nint.Zero &&
            Environment.TickCount64 - _lastFallbackTick >= FallbackThrottleMilliseconds)
        {
            _lastFallbackTick = Environment.TickCount64;
            return FindPreviewOverrideInWindow(
                fallbackTopLevelWindow,
                screenX,
                screenY,
                allowDockPane);
        }

        return null;
    }

    private static PreviewOverride? FindPreviewOverrideInWindow(
        nint topLevelWindow,
        double x,
        double y,
        bool allowDockPane)
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
        WpfRect? section = null;
        var visited = 0;
        Search(element, 0);

        if (toolbar is { } toolbarBounds)
        {
            return new PreviewOverride(toolbarBounds, IsDockPane: false);
        }

        if (section is { } sectionBounds)
        {
            return new PreviewOverride(sectionBounds, IsDockPane: false);
        }

        if (pane is { } paneBounds)
        {
            if (allowDockPane)
            {
                return new PreviewOverride(paneBounds, IsDockPane: true);
            }
        }

        return null;

        void Search(AutomationElement currentElement, int depth)
        {
            if (depth > MaxAncestorDepth || visited >= MaxVisitedElements)
            {
                return;
            }

            visited++;
            AutomationElement.AutomationElementInformation current;
            try
            {
                current = currentElement.Current;
            }
            catch (Exception)
            {
                return;
            }

            var bounds = current.BoundingRectangle;
            if (!bounds.Contains(x, y))
            {
                return;
            }

            if (toolbar is null &&
                IsToolbar(current.ControlType.ProgrammaticName, current.ClassName) &&
                HasArea(bounds))
            {
                toolbar = bounds;
            }

            if (section is null &&
                IsSection(current.ControlType.ProgrammaticName, current.ClassName) &&
                HasArea(bounds))
            {
                section = bounds;
            }

            if (pane is null &&
                IsDockedPane(current.ControlType.ProgrammaticName, current.ClassName) &&
                HasArea(bounds))
            {
                pane = bounds;
            }

            AutomationElement? child;
            try
            {
                child = TreeWalker.RawViewWalker.GetFirstChild(currentElement);
            }
            catch (Exception)
            {
                return;
            }

            while (child is not null)
            {
                if (toolbar is not null && section is not null && pane is not null)
                {
                    return;
                }

                Search(child, depth + 1);
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

    private static bool IsDockedPane(string controlTypeName, string className)
    {
        return controlTypeName == "ControlType.Pane" &&
               DockedPaneClassNames.Contains(className);
    }

    private static bool IsToolbar(string controlTypeName, string className)
    {
        return controlTypeName == "ControlType.ToolBar" ||
               className.Contains("toolbar-container", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSection(string controlTypeName, string className)
    {
        return controlTypeName == "ControlType.Tree" ||
               SectionClassNames.Contains(className);
    }

    private static bool HasArea(WpfRect bounds)
    {
        return !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0;
    }
}
