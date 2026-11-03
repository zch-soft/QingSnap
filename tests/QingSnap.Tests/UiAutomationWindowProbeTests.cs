using System.Windows;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class UiAutomationWindowProbeTests
{
    private static UiAutomationNode Node(
        string controlType,
        string className,
        double left,
        double top,
        double width,
        double height)
    {
        return new UiAutomationNode(controlType, className, new Rect(left, top, width, height));
    }

    [Fact]
    public void SolutionExplorerSelectsTabGroupContainerIncludingTabs()
    {
        var chain = new[]
        {
            Node("ControlType.Tree", "TreeView", 49, 268, 525, 1183),
            Node("ControlType.Pane", "GenericPane", 49, 268, 525, 1183),
            Node("ControlType.Pane", "ViewPresenter", 49, 188, 525, 1263),
            Node("ControlType.TabItem", "TabItem", 47, 1451, 166, 32),
            Node("ControlType.Tab", "ToolWindowTabGroup", 47, 176, 529, 1306),
            Node("ControlType.Pane", "ToolWindowTabGroupContainer", 47, 153, 529, 1330),
            Node("ControlType.Pane", "DockRoot", 38, 144, 2484, 1348),
            Node("ControlType.Window", "Window", -11, -11, 2582, 1550)
        };

        var pane = UiAutomationWindowProbe.SelectDockedPane(chain);

        Assert.Equal(new Rect(47, 153, 529, 1330), pane);
    }

    [Fact]
    public void SingleToolWindowSelectsViewPresenterIncludingTitleBar()
    {
        var chain = new[]
        {
            Node("ControlType.Pane", "RulersLayer", 1465, 228, 550, 929),
            Node("ControlType.Pane", "HwndHost", 1465, 228, 550, 929),
            Node("ControlType.Pane", "GenericPane", 1465, 228, 576, 956),
            Node("ControlType.Pane", "ViewPresenter", 1463, 153, 580, 1033),
            Node("ControlType.Pane", "DockRoot", 38, 144, 2484, 1348),
            Node("ControlType.Window", "Window", -11, -11, 2582, 1550)
        };

        var pane = UiAutomationWindowProbe.SelectDockedPane(chain);

        Assert.Equal(new Rect(1463, 153, 580, 1033), pane);
    }

    [Fact]
    public void EditorWithoutDockedPaneReturnsNull()
    {
        var chain = new[]
        {
            Node("ControlType.Pane", "DockRoot", 38, 144, 2484, 1348),
            Node("ControlType.Window", "Window", -11, -11, 2582, 1550)
        };

        Assert.Null(UiAutomationWindowProbe.SelectDockedPane(chain));
    }

    [Fact]
    public void WindowAncestorStopsTheSearch()
    {
        var chain = new[]
        {
            Node("ControlType.Pane", "ViewPresenter", 0, 0, 100, 100),
            Node("ControlType.Window", "Window", 0, 0, 500, 500),
            Node("ControlType.Pane", "ViewPresenter", 0, 0, 800, 800)
        };

        var pane = UiAutomationWindowProbe.SelectDockedPane(chain);

        Assert.Equal(new Rect(0, 0, 100, 100), pane);
    }

    [Fact]
    public void ToolbarWinsOverPaneWhenPointerIsOverToolbar()
    {
        var chain = new[]
        {
            Node("ControlType.ComboBox", "ComboBox", 1090, 55, 195, 35),
            Node("ControlType.ToolBar", "ToolBar", 14, 51, 1389, 42),
            Node("ControlType.Pane", "ToolBarDockTop", 14, 51, 2318, 84),
            Node("ControlType.Window", "Window", 0, 0, 2560, 1600)
        };

        var target = UiAutomationWindowProbe.SelectTarget(chain, allowDockPane: true);

        Assert.Equal(new Rect(14, 51, 1389, 42), target!.Value.Bounds);
        Assert.False(target.Value.IsDockPane);
    }

    [Fact]
    public void PaneIsUsedWhenNoToolbarIsUnderPointer()
    {
        var chain = new[]
        {
            Node("ControlType.Pane", "GenericPane", 1465, 228, 576, 956),
            Node("ControlType.Pane", "ViewPresenter", 1463, 153, 580, 1033),
            Node("ControlType.Window", "Window", 0, 0, 2560, 1600)
        };

        var target = UiAutomationWindowProbe.SelectTarget(chain, allowDockPane: true);

        Assert.Equal(new Rect(1463, 153, 580, 1033), target!.Value.Bounds);
        Assert.True(target.Value.IsDockPane);
    }

    [Fact]
    public void NoTargetForPlainWindowContent()
    {
        var chain = new[]
        {
            Node("ControlType.Document", "Editor", 0, 100, 1200, 800),
            Node("ControlType.Window", "Window", 0, 0, 2560, 1600)
        };

        Assert.Null(UiAutomationWindowProbe.SelectTarget(chain, allowDockPane: true));
    }

    [Fact]
    public void SectionWinsOverPaneWhenContentSectionIsPresent()
    {
        var chain = new[]
        {
            Node("ControlType.Custom", "ScriptedControl", 2054, 188, 457, 519),
            Node("ControlType.Pane", "GenericPane", 2054, 188, 457, 996),
            Node("ControlType.Pane", "ViewPresenter", 2052, 153, 461, 1033),
            Node("ControlType.Window", "Window", 0, 0, 2560, 1600)
        };

        var target = UiAutomationWindowProbe.SelectTarget(chain, allowDockPane: true);

        Assert.Equal(new Rect(2054, 188, 457, 519), target!.Value.Bounds);
        Assert.False(target.Value.IsDockPane);
    }

    [Fact]
    public void SolutionExplorerHeuristicSplitsTitleToolbarAndContent()
    {
        var pane = new Rect(47, 153, 529, 1330);

        var title = UiAutomationWindowProbe.SplitSolutionExplorer(pane, 160, 1);
        Assert.True(title.IsDockPane);
        Assert.Equal(pane, title.Bounds);

        var toolbar = UiAutomationWindowProbe.SplitSolutionExplorer(pane, 200, 1);
        Assert.False(toolbar.IsDockPane);
        Assert.Equal(new Rect(47, 176, 529, 54), toolbar.Bounds);

        var content = UiAutomationWindowProbe.SplitSolutionExplorer(pane, 600, 1);
        Assert.False(content.IsDockPane);
        Assert.Equal(new Rect(47, 230, 529, 1253), content.Bounds);
    }
}
