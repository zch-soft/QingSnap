using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QingSnap.App.Services;

public enum PortableModeSwitchStatus
{
    AlreadyInState,
    Switched,
    Failed
}

public sealed record PortableModeSwitchResult(
    PortableModeSwitchStatus Status,
    string Message,
    string? DataDirectory = null);

/// <summary>
/// 默认即为便携模式（数据放在程序目录 Data 下）；要退出便携模式，
/// 在程序目录放置 portable.off。切换在下次启动时完成迁移，避免运行中复制被占用的 OCR 原生库。
/// </summary>
public static class PortableModeService
{
    public const string OptOutFileName = "portable.off";
    public const string PendingSwitchFileName = "portable.switch";
    public const string SettingsFileName = "settings.json";

    public static string OptOutPath(string baseDirectory) =>
        Path.Combine(baseDirectory, OptOutFileName);

    public static string PendingSwitchPath(string baseDirectory) =>
        Path.Combine(baseDirectory, PendingSwitchFileName);

    public static bool IsEnabled(string baseDirectory) =>
        !File.Exists(OptOutPath(baseDirectory));

    public static string GetPortableDataDirectory(string baseDirectory) =>
        Path.Combine(baseDirectory, "Data");

    public static string GetInstalledDataDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QingSnap");

    public static string ResolveDataDirectory(string baseDirectory, string? installedDataDirectory = null) =>
        IsEnabled(baseDirectory)
            ? GetPortableDataDirectory(baseDirectory)
            : installedDataDirectory ?? GetInstalledDataDirectory();

    public static void RequestSwitch(string baseDirectory, bool enable)
    {
        Directory.CreateDirectory(baseDirectory);
        File.WriteAllText(
            PendingSwitchPath(baseDirectory),
            enable ? "enable" : "disable");
    }

    public static bool TryApplyPendingSwitch(
        string baseDirectory,
        string? installedDataDirectory = null)
    {
        var pendingPath = PendingSwitchPath(baseDirectory);
        if (!File.Exists(pendingPath))
        {
            return false;
        }

        bool enable;
        try
        {
            enable = File.ReadAllText(pendingPath).Trim()
                .Equals("enable", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warning("PortableMode", $"读取待切换标记失败：{exception.Message}");
            return false;
        }

        var result = Switch(baseDirectory, enable, installedDataDirectory);
        if (result.Status is PortableModeSwitchStatus.Switched or PortableModeSwitchStatus.AlreadyInState)
        {
            TryDeletePendingSwitch(pendingPath);
            return result.Status == PortableModeSwitchStatus.Switched;
        }

        DiagnosticLog.Warning("PortableMode", $"数据目录切换未完成：{result.Message}");
        return false;
    }

    public static PortableModeSwitchResult Switch(
        string baseDirectory,
        bool enable,
        string? installedDataDirectory = null)
    {
        var installedDirectory = installedDataDirectory ?? GetInstalledDataDirectory();
        var currentlyEnabled = IsEnabled(baseDirectory);
        if (currentlyEnabled == enable)
        {
            return new PortableModeSwitchResult(
                PortableModeSwitchStatus.AlreadyInState,
                "数据位置没有变化。",
                ResolveDataDirectory(baseDirectory, installedDirectory));
        }

        var source = currentlyEnabled ? GetPortableDataDirectory(baseDirectory) : installedDirectory;
        var target = enable ? GetPortableDataDirectory(baseDirectory) : installedDirectory;

        try
        {
            CopyDirectory(source, target);
            RetargetHistoryDirectory(source, target);

            if (enable)
            {
                File.Delete(OptOutPath(baseDirectory));
            }
            else
            {
                Directory.CreateDirectory(baseDirectory);
                File.WriteAllText(OptOutPath(baseDirectory), string.Empty);
            }

            return new PortableModeSwitchResult(
                PortableModeSwitchStatus.Switched,
                enable
                    ? $"已切换到便携模式，数据位于 {target}。"
                    : $"已退出便携模式，数据位于 {target}。",
                target);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new PortableModeSwitchResult(
                PortableModeSwitchStatus.Failed,
                $"切换失败：{exception.Message}",
                ResolveDataDirectory(baseDirectory, installedDirectory));
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void RetargetHistoryDirectory(string source, string target)
    {
        var settingsPath = Path.Combine(target, SettingsFileName);
        if (!File.Exists(settingsPath))
        {
            return;
        }

        var sourceHistory = Path.GetFullPath(Path.Combine(source, "History"));
        try
        {
            if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject settings ||
                settings["HistoryDirectory"] is not JsonValue value ||
                !value.TryGetValue<string>(out var historyPath) ||
                string.IsNullOrWhiteSpace(historyPath))
            {
                return;
            }

            if (!PathsEqual(Path.GetFullPath(historyPath), sourceHistory))
            {
                return;
            }

            settings["HistoryDirectory"] = Path.Combine(target, "History");
            File.WriteAllText(
                settingsPath,
                settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeletePendingSwitch(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warning("PortableMode", $"删除待切换标记失败：{exception.Message}");
        }
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            first.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            second.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
