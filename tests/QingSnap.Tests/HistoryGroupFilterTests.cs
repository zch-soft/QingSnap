using System.IO;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class HistoryGroupFilterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "QingSnap-history-group-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FoldingKeepsTheFirstItemOfEachGroup()
    {
        var first = CreateItem("a.png", "2026-10/group");
        var second = CreateItem("b.png", "2026-10/group");
        var ungrouped = CreateItem("c.png", null);
        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(HistoryGroupFilter.ShouldShow(first, fold: true, visible));
        Assert.False(HistoryGroupFilter.ShouldShow(second, fold: true, visible));
        Assert.True(HistoryGroupFilter.ShouldShow(ungrouped, fold: true, visible));
        Assert.True(HistoryGroupFilter.ShouldShow(second, fold: false, visible));
    }

    [Fact]
    public void GroupManifestIsResolvedPerDirectory()
    {
        Directory.CreateDirectory(_directory);
        SelectionGroupStore.Write(_directory, new SelectionGroupManifest(
            SelectionGroupManifest.CurrentVersion,
            "2026-10/abcd",
            SelectionMode.Matrix,
            2,
            3,
            5,
            DateTimeOffset.UtcNow));
        var cache = new Dictionary<string, SelectionGroupManifest?>(StringComparer.OrdinalIgnoreCase);

        var first = HistoryGroupFilter.ReadGroupForFile(Path.Combine(_directory, "a.png"), cache);
        var second = HistoryGroupFilter.ReadGroupForFile(Path.Combine(_directory, "b.png"), cache);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Single(cache);
        Assert.Equal("矩阵 2×3 · 5 张", first!.DisplayLabel);
    }

    [Fact]
    public void ItemWithoutGroupIsNeverHidden()
    {
        var item = CreateItem("a.png", null);
        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(HistoryGroupFilter.ShouldShow(item, fold: true, visible));
        Assert.False(item.HasGroup);
    }

    private static HistoryItem CreateItem(string fileName, string? groupId) =>
        new(
            Path.Combine(Path.GetTempPath(), fileName),
            fileName,
            DateTime.Now,
            10,
            10,
            100,
            null,
            false,
            string.Empty,
            GroupId: groupId,
            GroupLabel: groupId is null ? null : "矩阵 2×2 · 3 张",
            GroupCount: groupId is null ? 0 : 3);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }
        catch (IOException)
        {
        }
    }
}
