using System.IO;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class SelectionGroupStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "QingSnap-group-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ManifestRoundTrips()
    {
        var manifest = new SelectionGroupManifest(
            SelectionGroupManifest.CurrentVersion,
            "2026-10/20261025-163500",
            SelectionMode.Matrix,
            3,
            2,
            5,
            DateTimeOffset.UtcNow);

        SelectionGroupStore.Write(_directory, manifest);
        var loaded = SelectionGroupStore.TryRead(_directory);

        Assert.NotNull(loaded);
        Assert.Equal(manifest.GroupId, loaded!.GroupId);
        Assert.Equal(SelectionMode.Matrix, loaded.Mode);
        Assert.Equal(3, loaded.Rows);
        Assert.Equal(2, loaded.Columns);
        Assert.Equal(5, loaded.Count);
        Assert.Equal("矩阵 3×2 · 5 张", loaded.DisplayLabel);
    }

    [Fact]
    public void ManifestCanBeReadFromAFilePath()
    {
        SelectionGroupStore.Write(_directory, new SelectionGroupManifest(
            SelectionGroupManifest.CurrentVersion,
            "2026-10/abcd",
            SelectionMode.Free,
            0,
            0,
            2,
            DateTimeOffset.UtcNow));

        var loaded = SelectionGroupStore.TryReadForFile(Path.Combine(_directory, "QingSnap_1.png"));

        Assert.NotNull(loaded);
        Assert.Equal("多图 · 2 张", loaded!.DisplayLabel);
    }

    [Fact]
    public void MissingOrCorruptedManifestIsIgnored()
    {
        Assert.Null(SelectionGroupStore.TryRead(_directory));

        Directory.CreateDirectory(_directory);
        File.WriteAllText(SelectionGroupStore.GetManifestPath(_directory), "{ not json");

        Assert.Null(SelectionGroupStore.TryRead(_directory));
    }

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
