using System.IO;
using System.Text.Json;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class PortableModeServiceTests
{
    [Fact]
    public void Switch_DisablesPortableModeCopiesDataAndWritesOptOut()
    {
        var root = CreateRoot();
        var appDirectory = Path.Combine(root, "app");
        var installedDirectory = Path.Combine(root, "local");
        try
        {
            var portableDirectory = PortableModeService.GetPortableDataDirectory(appDirectory);
            Directory.CreateDirectory(Path.Combine(portableDirectory, "History"));
            File.WriteAllText(Path.Combine(portableDirectory, "History", "shot.png"), "image");
            File.WriteAllText(
                Path.Combine(portableDirectory, "settings.json"),
                SettingsJson(Path.Combine(portableDirectory, "History")));

            var result = PortableModeService.Switch(appDirectory, enable: false, installedDirectory);

            Assert.Equal(PortableModeSwitchStatus.Switched, result.Status);
            Assert.False(PortableModeService.IsEnabled(appDirectory));
            Assert.True(File.Exists(PortableModeService.OptOutPath(appDirectory)));
            Assert.True(File.Exists(Path.Combine(installedDirectory, "History", "shot.png")));
            Assert.Equal(
                Path.Combine(installedDirectory, "History"),
                ReadHistoryDirectory(Path.Combine(installedDirectory, "settings.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Switch_EnablesPortableModeRemovesOptOutAndCopiesBack()
    {
        var root = CreateRoot();
        var appDirectory = Path.Combine(root, "app");
        var installedDirectory = Path.Combine(root, "local");
        try
        {
            Directory.CreateDirectory(Path.Combine(installedDirectory, "History"));
            File.WriteAllText(Path.Combine(installedDirectory, "History", "shot.png"), "image");
            File.WriteAllText(
                Path.Combine(installedDirectory, "settings.json"),
                SettingsJson(Path.Combine(installedDirectory, "History")));
            Directory.CreateDirectory(appDirectory);
            File.WriteAllText(PortableModeService.OptOutPath(appDirectory), string.Empty);

            var result = PortableModeService.Switch(appDirectory, enable: true, installedDirectory);

            Assert.Equal(PortableModeSwitchStatus.Switched, result.Status);
            Assert.True(PortableModeService.IsEnabled(appDirectory));
            Assert.False(File.Exists(PortableModeService.OptOutPath(appDirectory)));
            var portableDirectory = PortableModeService.GetPortableDataDirectory(appDirectory);
            Assert.True(File.Exists(Path.Combine(portableDirectory, "History", "shot.png")));
            Assert.Equal(
                Path.Combine(portableDirectory, "History"),
                ReadHistoryDirectory(Path.Combine(portableDirectory, "settings.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Switch_OverwritesStaleTargetData()
    {
        var root = CreateRoot();
        var appDirectory = Path.Combine(root, "app");
        var installedDirectory = Path.Combine(root, "local");
        try
        {
            var portableDirectory = PortableModeService.GetPortableDataDirectory(appDirectory);
            Directory.CreateDirectory(portableDirectory);
            File.WriteAllText(
                Path.Combine(portableDirectory, "settings.json"),
                SettingsJson(Path.Combine(portableDirectory, "History")));
            Directory.CreateDirectory(installedDirectory);
            File.WriteAllText(
                Path.Combine(installedDirectory, "settings.json"),
                SettingsJson(Path.Combine(installedDirectory, "History")));
            File.WriteAllText(PortableModeService.OptOutPath(appDirectory), string.Empty);

            var result = PortableModeService.Switch(appDirectory, enable: true, installedDirectory);

            Assert.Equal(PortableModeSwitchStatus.Switched, result.Status);
            Assert.Equal(
                Path.Combine(portableDirectory, "History"),
                ReadHistoryDirectory(Path.Combine(portableDirectory, "settings.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Switch_WithoutExistingDataStillWritesOptOut()
    {
        var root = CreateRoot();
        var appDirectory = Path.Combine(root, "app");
        try
        {
            var result = PortableModeService.Switch(appDirectory, enable: false, Path.Combine(root, "local"));

            Assert.Equal(PortableModeSwitchStatus.Switched, result.Status);
            Assert.False(PortableModeService.IsEnabled(appDirectory));
            Assert.True(File.Exists(PortableModeService.OptOutPath(appDirectory)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Switch_ReportsAlreadyInState()
    {
        var root = CreateRoot();
        var appDirectory = Path.Combine(root, "app");
        try
        {
            var result = PortableModeService.Switch(appDirectory, enable: true, Path.Combine(root, "local"));

            Assert.Equal(PortableModeSwitchStatus.AlreadyInState, result.Status);
            Assert.True(PortableModeService.IsEnabled(appDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveDataDirectory_FollowsOptOutMarker()
    {
        var root = CreateRoot();
        var appDirectory = Path.Combine(root, "app");
        var installedDirectory = Path.Combine(root, "local");
        try
        {
            Assert.Equal(
                PortableModeService.GetPortableDataDirectory(appDirectory),
                PortableModeService.ResolveDataDirectory(appDirectory, installedDirectory));

            Directory.CreateDirectory(appDirectory);
            File.WriteAllText(PortableModeService.OptOutPath(appDirectory), string.Empty);

            Assert.Equal(
                installedDirectory,
                PortableModeService.ResolveDataDirectory(appDirectory, installedDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PendingSwitch_IsAppliedOnStartupAndClearsMarker()
    {
        var root = CreateRoot();
        var appDirectory = Path.Combine(root, "app");
        var installedDirectory = Path.Combine(root, "local");
        try
        {
            var portableDirectory = PortableModeService.GetPortableDataDirectory(appDirectory);
            Directory.CreateDirectory(portableDirectory);
            File.WriteAllText(
                Path.Combine(portableDirectory, "settings.json"),
                SettingsJson(Path.Combine(portableDirectory, "History")));

            PortableModeService.RequestSwitch(appDirectory, enable: false);

            Assert.True(File.Exists(PortableModeService.PendingSwitchPath(appDirectory)));
            Assert.True(PortableModeService.IsEnabled(appDirectory));

            Assert.True(PortableModeService.TryApplyPendingSwitch(appDirectory, installedDirectory));

            Assert.False(PortableModeService.IsEnabled(appDirectory));
            Assert.False(File.Exists(PortableModeService.PendingSwitchPath(appDirectory)));
            Assert.True(File.Exists(Path.Combine(installedDirectory, "settings.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PendingSwitch_ReturnsFalseWhenNothingIsPending()
    {
        var root = CreateRoot();
        var appDirectory = Path.Combine(root, "app");
        try
        {
            Assert.False(PortableModeService.TryApplyPendingSwitch(appDirectory, Path.Combine(root, "local")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveLogDirectory_FollowsPortableMode()
    {
        var root = CreateRoot();
        var appDirectory = Path.Combine(root, "app");
        var installedDirectory = Path.Combine(root, "local");
        try
        {
            Assert.Equal(
                Path.Combine(PortableModeService.GetPortableDataDirectory(appDirectory), "Logs"),
                DiagnosticLog.ResolveLogDirectory(appDirectory, installedDirectory));

            Directory.CreateDirectory(appDirectory);
            File.WriteAllText(PortableModeService.OptOutPath(appDirectory), string.Empty);

            Assert.Equal(
                Path.Combine(installedDirectory, "Logs"),
                DiagnosticLog.ResolveLogDirectory(appDirectory, installedDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "QingSnap.Tests", "portable", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string SettingsJson(string historyDirectory) =>
        JsonSerializer.Serialize(new { HistoryDirectory = historyDirectory });

    private static string ReadHistoryDirectory(string settingsPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        return document.RootElement.GetProperty("HistoryDirectory").GetString() ?? string.Empty;
    }
}
