using System.Windows;
using System.Windows.Automation;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class UiAutomationWindowProbeTests
{
    [Fact]
    public void ToolbarShapeMatchesTopRowOnly()
    {
        var pane = new Rect(2052, 153, 461, 1033);

        Assert.True(UiAutomationWindowProbe.IsToolbarShape(
            ControlType.Group,
            new Rect(2054, 188, 430, 42),
            pane));

        // A taller header block is not the toolbar row.
        Assert.False(UiAutomationWindowProbe.IsToolbarShape(
            ControlType.Group,
            new Rect(2054, 188, 430, 107),
            pane));

        // The whole content host is not a toolbar.
        Assert.False(UiAutomationWindowProbe.IsToolbarShape(
            ControlType.Pane,
            new Rect(2054, 188, 457, 996),
            pane));
    }

    [Fact]
    public void ToolbarShapeRejectsTextAndButtons()
    {
        var pane = new Rect(0, 0, 1000, 800);

        Assert.False(UiAutomationWindowProbe.IsToolbarShape(
            ControlType.Text,
            new Rect(0, 0, 900, 40),
            pane));
        Assert.False(UiAutomationWindowProbe.IsToolbarShape(
            ControlType.Button,
            new Rect(0, 0, 900, 40),
            pane));
    }

    [Fact]
    public void SectionShapeUsesPaneAreaRatio()
    {
        var pane = new Rect(2052, 153, 461, 1033);

        Assert.True(UiAutomationWindowProbe.IsSectionShape(
            new Rect(2054, 188, 457, 519),
            pane));
        Assert.True(UiAutomationWindowProbe.IsSectionShape(
            new Rect(2054, 715, 457, 469),
            pane));
        Assert.False(UiAutomationWindowProbe.IsSectionShape(
            new Rect(2054, 294, 430, 47),
            pane));
    }
}
