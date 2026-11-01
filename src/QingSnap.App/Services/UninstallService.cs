using System.IO;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed record UninstallTarget(
    string Key,
    string Label,
    string Path,
    bool Exists,
    bool CanDelete,
    string? Note);

public sealed record UninstallResult(
    IReadOnlyList<string> Deleted,
    IReadOnlyList<string> Errors,
    string? ErrorLogPath = null,
    bool LogsScheduled = false,
    IReadOnlyList<string>? Pending = null);

public sealed class UninstallService
{
    private const int DeleteAttempts = 6;
    private const int RetryDelayMilliseconds = 250;

    private readonly string _statePath;
    private readonly string _appDataDirectory;
    private readonly string _logDirectory;
    private readonly string _tempDirectory;

    public UninstallService(
        string? statePath = null,
        string? logDirectory = null,
        string? tempDirectory = null)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _statePath = Path.GetFullPath(statePath ?? Path.Combine(
            localAppData,
            "QingSnap",
            "state.json"));
        _appDataDirectory = Path.GetDirectoryName(_statePath)!;
        _logDirectory = Path.GetFullPath(logDirectory ?? Path.Combine(_appDataDirectory, "Logs"));
        _tempDirectory = Path.GetFullPath(tempDirectory ?? Path.GetTempPath());
    }

    public string LogDirectory => _logDirectory;

    public IReadOnlyList<UninstallTarget> Describe(AppSettings settings, string dataDirectory)
    {
        var data = Path.GetFullPath(dataDirectory);
        var historyDirectory = ResolveHistoryDirectory(settings, data);
        var databasePath = Path.Combine(data, "history-metadata.db");
        var settingsPath = Path.Combine(data, "settings.json");
        var ocrPath = Path.Combine(data, "Ocr");
        var historySafe = IsSafeHistoryDirectory(historyDirectory, data, AppContext.BaseDirectory);
        var isDefaultHistory = string.Equals(
            NormalizePath(historyDirectory),
            NormalizePath(Path.Combine(data, "History")),
            StringComparison.OrdinalIgnoreCase);

        return
        [
            new UninstallTarget(
                "history",
                "截图",
                historyDirectory,
                Directory.Exists(historyDirectory),
                historySafe,
                historySafe
                    ? isDefaultHistory ? null : "自定义目录，将只删除其中的文件"
                    : "该目录包含程序或数据目录，不能自动删除"),
            new UninstallTarget(
                "database",
                "数据库",
                databasePath,
                ExistingDatabaseFiles(data).Any(File.Exists),
                true,
                null),
            new UninstallTarget(
                "settings",
                "设置",
                settingsPath,
                File.Exists(settingsPath) || File.Exists(_statePath),
                true,
                null),
            new UninstallTarget(
                "ocr",
                "OCR 模型",
                ocrPath,
                Directory.Exists(ocrPath),
                true,
                null),
            new UninstallTarget(
                "logs",
                "日志与临时文件",
                _logDirectory,
                LogTargetsExist(),
                true,
                null)
        ];
    }

    public UninstallResult Delete(
        UninstallOptions options,
        AppSettings settings,
        string dataDirectory)
    {
        var data = Path.GetFullPath(dataDirectory);
        var deleted = new List<string>();
        var errors = new List<string>();

        if (options.DeleteHistory)
        {
            DeleteHistory(settings, data, deleted, errors);
        }

        if (options.DeleteDatabase)
        {
            foreach (var databaseFile in ExistingDatabaseFiles(data))
            {
                DeleteFileIfExists(databaseFile, deleted, errors);
            }
        }

        if (options.DeleteSettings)
        {
            foreach (var settingsFile in SettingsFiles(data))
            {
                DeleteFileIfExists(settingsFile, deleted, errors);
            }
        }

        if (options.DeleteOcr)
        {
            DeleteDirectoryIfExists(Path.Combine(data, "Ocr"), deleted, errors);
            DeleteDirectoryIfExists(
                Path.Combine(AppContext.BaseDirectory, "AdvancedOcr"),
                deleted,
                errors);
        }

        if (options.DeleteLogs)
        {
            DeleteLogs(deleted, errors);
        }

        var pending = new List<string>();
        CollectPending(options, data, pending);
        if (pending.Count > 0)
        {
            errors.RemoveAll(error => pending.Any(path =>
                error.StartsWith(path, StringComparison.OrdinalIgnoreCase)));
        }

        TryDeleteEmptyDirectory(data);
        TryDeleteEmptyDirectory(_appDataDirectory);

        return new UninstallResult(deleted, errors, Pending: pending);
    }

    internal static bool IsSafeHistoryDirectory(
        string historyDirectory,
        string dataDirectory,
        string executableDirectory)
    {
        string history;
        string data;
        string executable;
        try
        {
            history = NormalizePath(historyDirectory);
            data = NormalizePath(dataDirectory);
            executable = NormalizePath(executableDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (history.Length == 0)
        {
            return false;
        }

        var root = Path.GetPathRoot(history);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(NormalizePath(root), history, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsSameOrChild(data, history) || IsSameOrChild(executable, history))
        {
            return false;
        }

        foreach (var protectedPath in ProtectedPaths())
        {
            if (!string.IsNullOrEmpty(protectedPath) &&
                IsSameOrChild(NormalizePath(protectedPath), history))
            {
                return false;
            }
        }

        return true;
    }

    private static void DeleteHistory(
        AppSettings settings,
        string dataDirectory,
        List<string> deleted,
        List<string> errors)
    {
        string historyDirectory;
        try
        {
            historyDirectory = ResolveHistoryDirectory(settings, dataDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add($"截图目录无效：{exception.Message}");
            return;
        }

        if (!IsSafeHistoryDirectory(historyDirectory, dataDirectory, AppContext.BaseDirectory))
        {
            errors.Add($"截图目录未删除（路径不安全）：{historyDirectory}");
            return;
        }

        var isDefaultHistory = string.Equals(
            NormalizePath(historyDirectory),
            NormalizePath(Path.Combine(dataDirectory, "History")),
            StringComparison.OrdinalIgnoreCase);
        if (isDefaultHistory)
        {
            DeleteDirectoryIfExists(historyDirectory, deleted, errors);
            return;
        }

        DeleteDirectoryContents(historyDirectory, deleted, errors);
    }

    private static string ResolveHistoryDirectory(AppSettings settings, string dataDirectory) =>
        string.IsNullOrWhiteSpace(settings.HistoryDirectory)
            ? Path.Combine(dataDirectory, "History")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.HistoryDirectory.Trim()));

    private static IEnumerable<string> ExistingDatabaseFiles(string dataDirectory)
    {
        var database = Path.Combine(dataDirectory, "history-metadata.db");
        yield return database;
        yield return database + "-wal";
        yield return database + "-shm";
    }

    private IEnumerable<string> SettingsFiles(string dataDirectory)
    {
        yield return Path.Combine(dataDirectory, "settings.json");
        yield return Path.Combine(dataDirectory, "state.json");
        yield return Path.Combine(dataDirectory, "selection-presets.json");
        yield return Path.Combine(dataDirectory, "update-state.json");
        yield return _statePath;
    }

    private void CollectPending(
        UninstallOptions options,
        string dataDirectory,
        List<string> pending)
    {
        if (options.DeleteOcr)
        {
            var ocrDirectory = Path.Combine(dataDirectory, "Ocr");
            if (Directory.Exists(ocrDirectory))
            {
                pending.Add(ocrDirectory);
            }

            var legacyDirectory = Path.Combine(AppContext.BaseDirectory, "AdvancedOcr");
            if (Directory.Exists(legacyDirectory))
            {
                pending.Add(legacyDirectory);
            }
        }

        if (!options.DeleteLogs)
        {
            return;
        }

        if (Directory.Exists(_logDirectory))
        {
            pending.Add(_logDirectory);
        }

        var tempDirectory = Path.Combine(_tempDirectory, "QingSnap");
        if (Directory.Exists(tempDirectory))
        {
            pending.Add(tempDirectory);
        }

        var crashLog = Path.Combine(_tempDirectory, "QingSnap-crash.log");
        if (File.Exists(crashLog))
        {
            pending.Add(crashLog);
        }

        pending.AddRange(OcrPackagesInTemp());
    }

    private void DeleteLogs(List<string> deleted, List<string> errors)
    {
        DeleteDirectoryIfExists(_logDirectory, deleted, errors);
        DeleteDirectoryIfExists(Path.Combine(_tempDirectory, "QingSnap"), deleted, errors);
        DeleteFileIfExists(Path.Combine(_tempDirectory, "QingSnap-crash.log"), deleted, errors);

        foreach (var package in OcrPackagesInTemp())
        {
            DeleteFileIfExists(package, deleted, errors);
        }
    }

    private bool LogTargetsExist() =>
        Directory.Exists(_logDirectory) ||
        Directory.Exists(Path.Combine(_tempDirectory, "QingSnap")) ||
        File.Exists(Path.Combine(_tempDirectory, "QingSnap-crash.log")) ||
        OcrPackagesInTemp().Any();

    private IEnumerable<string> OcrPackagesInTemp()
    {
        try
        {
            return Directory.EnumerateFiles(_tempDirectory, "QingSnap-OCR-Module-*.zip").ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteFileIfExists(string path, List<string> deleted, List<string> errors)
    {
        if (!File.Exists(path))
        {
            return;
        }

        for (var attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            try
            {
                File.Delete(path);
                deleted.Add(path);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == DeleteAttempts)
                {
                    errors.Add($"{path}: {exception.Message}");
                    return;
                }

                Thread.Sleep(RetryDelayMilliseconds);
            }
        }
    }

    private static void DeleteDirectoryIfExists(string path, List<string> deleted, List<string> errors)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                deleted.Add(path);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == DeleteAttempts)
                {
                    errors.Add($"{path}: {exception.Message}");
                    return;
                }

                Thread.Sleep(RetryDelayMilliseconds);
            }
        }
    }

    private static void DeleteDirectoryContents(string path, List<string> deleted, List<string> errors)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                DeleteFileIfExists(file, deleted, errors);
            }

            foreach (var directory in Directory.EnumerateDirectories(path))
            {
                DeleteDirectoryIfExists(directory, deleted, errors);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{path}: {exception.Message}");
        }
    }

    private static bool IsSameOrChild(string candidate, string parent) =>
        string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static IEnumerable<string> ProtectedPaths()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
    }
}
