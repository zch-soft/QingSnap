using System.IO;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class UninstallServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _dataDirectory;
    private readonly string _historyDirectory;
    private readonly string _statePath;

    public UninstallServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"QingSnap-uninstall-{Guid.NewGuid():N}");
        _dataDirectory = Path.Combine(_root, "Data");
        _historyDirectory = Path.Combine(_dataDirectory, "History");
        _statePath = Path.Combine(_root, "LocalAppData", "state.json");
        Directory.CreateDirectory(_historyDirectory);
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "Ocr", "Runtime"));
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Delete_WithNothingSelected_KeepsEverything()
    {
        Seed();

        var result = NewService().Delete(UninstallOptions.KeepAll, Settings(), _dataDirectory);

        Assert.Empty(result.Errors);
        Assert.True(File.Exists(Path.Combine(_dataDirectory, "settings.json")));
        Assert.True(File.Exists(Path.Combine(_dataDirectory, "history-metadata.db")));
        Assert.True(Directory.Exists(_historyDirectory));
        Assert.True(Directory.Exists(Path.Combine(_dataDirectory, "Ocr")));
        Assert.True(File.Exists(_statePath));
    }

    [Fact]
    public void Delete_SelectedCategories_RemovesOnlyThose()
    {
        Seed();

        var result = NewService().Delete(
            new UninstallOptions(DeleteHistory: true, DeleteDatabase: true, DeleteSettings: false, DeleteOcr: false),
            Settings(),
            _dataDirectory);

        Assert.Empty(result.Errors);
        Assert.False(Directory.Exists(_historyDirectory));
        Assert.False(File.Exists(Path.Combine(_dataDirectory, "history-metadata.db")));
        Assert.False(File.Exists(Path.Combine(_dataDirectory, "history-metadata.db-wal")));
        Assert.True(File.Exists(Path.Combine(_dataDirectory, "settings.json")));
        Assert.True(File.Exists(Path.Combine(_dataDirectory, "selection-presets.json")));
        Assert.True(Directory.Exists(Path.Combine(_dataDirectory, "Ocr")));
        Assert.True(File.Exists(_statePath));
    }

    [Fact]
    public void Delete_AllCategories_RemovesSelectedDataOnly()
    {
        Seed();
        var keepFile = Path.Combine(_dataDirectory, "keep.txt");
        File.WriteAllText(keepFile, "keep");

        var result = NewService().Delete(
            new UninstallOptions(true, true, true, true),
            Settings(),
            _dataDirectory);

        Assert.Empty(result.Errors);
        Assert.False(Directory.Exists(_historyDirectory));
        Assert.False(Directory.Exists(Path.Combine(_dataDirectory, "Ocr")));
        Assert.False(File.Exists(Path.Combine(_dataDirectory, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_dataDirectory, "state.json")));
        Assert.False(File.Exists(Path.Combine(_dataDirectory, "update-state.json")));
        Assert.False(File.Exists(_statePath));
        Assert.True(File.Exists(keepFile));
    }

    [Fact]
    public void Delete_MissingTargets_DoesNotThrow()
    {
        var emptyData = Path.Combine(_root, "Empty");
        Directory.CreateDirectory(emptyData);

        var result = NewService().Delete(
            new UninstallOptions(true, true, true, true),
            Settings(),
            emptyData);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Delete_CustomHistoryDirectory_RemovesContentsOnly()
    {
        var customHistory = Path.Combine(_root, "CustomHistory");
        var nested = Path.Combine(customHistory, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(customHistory, "a.png"), "a");
        File.WriteAllText(Path.Combine(nested, "b.png"), "b");
        var settings = new AppSettings { HistoryDirectory = customHistory };

        var result = NewService().Delete(
            new UninstallOptions(DeleteHistory: true, DeleteDatabase: false, DeleteSettings: false, DeleteOcr: false),
            settings,
            _dataDirectory);

        Assert.Empty(result.Errors);
        Assert.True(Directory.Exists(customHistory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(customHistory));
    }

    [Fact]
    public void Delete_UnsafeHistoryDirectory_IsRejected()
    {
        Seed();
        var unsafeHistory = _root;
        var settings = new AppSettings { HistoryDirectory = unsafeHistory };

        var result = NewService().Delete(
            new UninstallOptions(DeleteHistory: true, DeleteDatabase: false, DeleteSettings: false, DeleteOcr: false),
            settings,
            _dataDirectory);

        Assert.NotEmpty(result.Errors);
        Assert.True(Directory.Exists(unsafeHistory));
        Assert.True(File.Exists(Path.Combine(_dataDirectory, "settings.json")));
    }

    [Fact]
    public void Describe_ReportsCategoriesAndHistorySafety()
    {
        var service = NewService();
        var targets = service.Describe(Settings(), _dataDirectory);

        Assert.Equal(4, targets.Count);
        var history = Assert.Single(targets, target => target.Key == "history");
        Assert.True(history.CanDelete);
        Assert.Contains("database", targets.Select(target => target.Key));
        Assert.Contains("settings", targets.Select(target => target.Key));
        Assert.Contains("ocr", targets.Select(target => target.Key));

        var unsafeTargets = service.Describe(
            new AppSettings { HistoryDirectory = _root },
            _dataDirectory);
        var unsafeHistory = Assert.Single(unsafeTargets, target => target.Key == "history");
        Assert.False(unsafeHistory.CanDelete);
        Assert.False(string.IsNullOrWhiteSpace(unsafeHistory.Note));
    }

    private UninstallService NewService() => new(_statePath);

    private AppSettings Settings() => new() { HistoryDirectory = _historyDirectory };

    private void Seed()
    {
        File.WriteAllText(Path.Combine(_dataDirectory, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(_dataDirectory, "state.json"), "{}");
        File.WriteAllText(Path.Combine(_dataDirectory, "selection-presets.json"), "[]");
        File.WriteAllText(Path.Combine(_dataDirectory, "update-state.json"), "{}");
        File.WriteAllText(Path.Combine(_dataDirectory, "history-metadata.db"), "db");
        File.WriteAllText(Path.Combine(_dataDirectory, "history-metadata.db-wal"), "wal");
        File.WriteAllText(Path.Combine(_historyDirectory, "shot.png"), "png");
        File.WriteAllText(Path.Combine(_dataDirectory, "Ocr", "Runtime", "engine.dll"), "dll");
        File.WriteAllText(_statePath, "{}");
    }
}
