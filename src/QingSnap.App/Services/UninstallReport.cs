using System.IO;
using System.Reflection;
using System.Text;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

internal static class UninstallReport
{
    public static string Write(
        UninstallResult result,
        UninstallOptions options,
        string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        var path = Path.Combine(
            logDirectory,
            $"Uninstall-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var builder = new StringBuilder();
        builder.AppendLine($"QingSnap 卸载报告 / uninstall report");
        builder.AppendLine($"Time: {DateTimeOffset.Now:O}");
        builder.AppendLine($"Version: {Assembly.GetEntryAssembly()?.GetName().Version}");
        builder.AppendLine(
            $"Options: history={options.DeleteHistory} database={options.DeleteDatabase} " +
            $"settings={options.DeleteSettings} ocr={options.DeleteOcr} logs={options.DeleteLogs}");
        builder.AppendLine($"Deleted: {result.Deleted.Count}");
        foreach (var item in result.Deleted)
        {
            builder.AppendLine($"  - {item}");
        }

        builder.AppendLine($"Errors: {result.Errors.Count}");
        foreach (var error in result.Errors)
        {
            builder.AppendLine($"  ! {error}");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        return path;
    }
}
