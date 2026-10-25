using System.IO;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class SelectionPresetStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "QingSnap-presets-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PresetsCanBeAddedRenamedAndDeleted()
    {
        var store = new SelectionPresetStore(_directory);
        var added = store.Add("报表区域", new SelectionSet(
            SelectionMode.Free,
            [new CaptureRegion(1, 2, 30, 40), new CaptureRegion(100, 120, 50, 60)]));

        var loaded = Assert.Single(store.LoadAll());
        Assert.Equal(added.Id, loaded.Id);
        Assert.Equal("报表区域", loaded.Name);
        Assert.Equal(2, loaded.Selection.Regions.Count);

        Assert.True(store.Rename(added.Id, "新的名字"));
        Assert.Equal("新的名字", store.Find(added.Id)!.Name);

        Assert.True(store.Delete(added.Id));
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void PresetsSurviveAStoreReload()
    {
        var first = new SelectionPresetStore(_directory);
        var added = first.Add("跨会话", SelectionSet.Single(new CaptureRegion(5, 6, 7, 8)));

        var second = new SelectionPresetStore(_directory);
        var loaded = Assert.Single(second.LoadAll());
        Assert.Equal(added.Id, loaded.Id);
        Assert.Equal("跨会话", loaded.Name);
        Assert.Equal(new CaptureRegion(5, 6, 7, 8), Assert.Single(loaded.Selection.Regions));
    }

    [Fact]
    public void CorruptedPresetFileDoesNotPreventSavingNewPresets()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "selection-presets.json"), "{ not json");
        var store = new SelectionPresetStore(_directory);

        Assert.Empty(store.LoadAll());

        store.Add("恢复", SelectionSet.Single(new CaptureRegion(0, 0, 10, 10)));
        Assert.Single(store.LoadAll());
    }

    [Fact]
    public void DuplicatePresetNamesAreAllowed()
    {
        var store = new SelectionPresetStore(_directory);
        store.Add("同名", SelectionSet.Single(new CaptureRegion(0, 0, 10, 10)));
        store.Add("同名", SelectionSet.Single(new CaptureRegion(20, 20, 10, 10)));

        Assert.Equal(2, store.LoadAll().Count);
    }

    [Fact]
    public void EmptyNamesAreReplaced()
    {
        var store = new SelectionPresetStore(_directory);
        var preset = store.Add("   ", SelectionSet.Single(new CaptureRegion(0, 0, 10, 10)));

        Assert.Equal("未命名选区", preset.Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
