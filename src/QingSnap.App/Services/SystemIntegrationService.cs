using System.IO;

namespace QingSnap.App.Services;

public sealed class SystemIntegrationService
{
    private readonly ShortcutService _shortcutService;
    private readonly UninstallRegistryService _registryService;

    public SystemIntegrationService(
        ShortcutService? shortcutService = null,
        UninstallRegistryService? registryService = null)
    {
        _shortcutService = shortcutService ?? new ShortcutService();
        _registryService = registryService ?? new UninstallRegistryService();
    }

    public bool IsStartMenuEntryPresent => _shortcutService.Exists();

    public bool IsRegisteredForUninstall => _registryService.IsRegistered();

    public void CreateStartMenuEntry()
    {
        _shortcutService.Create();
        _registryService.Register();
    }

    public void RemoveStartMenuEntry()
    {
        _shortcutService.Remove();
        _registryService.Remove();
    }

    internal static string StartupEntryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "QingSnap.cmd");

    public static void ApplyStartWithWindows(bool enabled)
    {
        var startupDirectory = Path.GetDirectoryName(StartupEntryPath)
            ?? throw new InvalidOperationException("无法确定启动目录。");
        Directory.CreateDirectory(startupDirectory);
        if (enabled)
        {
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定 QingSnap 程序路径。");
            File.WriteAllText(
                StartupEntryPath,
                $"@echo off{Environment.NewLine}start \"\" \"{executablePath}\"{Environment.NewLine}");
            return;
        }

        if (File.Exists(StartupEntryPath))
        {
            File.Delete(StartupEntryPath);
        }
    }

    public static void RemoveAutostartEntry() => ApplyStartWithWindows(false);
}
