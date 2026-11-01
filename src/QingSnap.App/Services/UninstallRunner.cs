using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;
using MessageBox = System.Windows.MessageBox;

namespace QingSnap.App.Services;

internal static class UninstallRunner
{
    public static UninstallResult Run(
        UninstallOptions options,
        AppSettingsService settingsService,
        SystemIntegrationService integrationService,
        UninstallService uninstallService)
    {
        var errors = new List<string>();

        if (!options.DeleteSettings)
        {
            try
            {
                settingsService.Save(settingsService.Current with { StartWithWindows = false });
            }
            catch (Exception exception)
            {
                errors.Add($"更新设置失败：{exception.Message}");
            }
        }

        try
        {
            SystemIntegrationService.RemoveAutostartEntry();
        }
        catch (Exception exception)
        {
            errors.Add($"移除开机启动项失败：{exception.Message}");
        }

        try
        {
            integrationService.RemoveStartMenuEntry();
        }
        catch (Exception exception)
        {
            errors.Add($"移除系统集成失败：{exception.Message}");
        }

        var result = uninstallService.Delete(options, settingsService.Current, settingsService.DataDirectory);
        result = result with { Errors = errors.Concat(result.Errors).ToArray() };

        if (result.Errors.Count == 0)
        {
            if (!options.DeleteLogs)
            {
                return result;
            }

            DiagnosticLog.Shutdown();
            RebootDeletion.ScheduleDirectory(uninstallService.LogDirectory);
            return result with { LogsScheduled = true };
        }

        var reportPath = UninstallReport.Write(result, options, uninstallService.LogDirectory);
        DiagnosticLog.Warning(
            "Uninstall",
            $"卸载有 {result.Errors.Count} 项未完成，详情见 {reportPath}。");
        return result with { ErrorLogPath = reportPath };
    }

    public static void ShowSummary(UninstallResult result)
    {
        var executableDirectory = Path.GetDirectoryName(
            Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

        if (result.ErrorLogPath is { } logPath)
        {
            var failure = new StringBuilder();
            failure.AppendLine("卸载未完全完成，以下项目未能删除：");
            failure.AppendLine();
            foreach (var error in result.Errors)
            {
                failure.AppendLine($"· {error}");
            }

            failure.AppendLine();
            failure.AppendLine($"失败原因已写入日志：{logPath}");
            failure.AppendLine($"程序文件夹：{executableDirectory}");
            failure.AppendLine();
            failure.Append("是否打开日志文件夹？");
            var openLogs = MessageBox.Show(
                failure.ToString(),
                "QingSnap 卸载未完成",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (openLogs == MessageBoxResult.Yes)
            {
                OpenInExplorer(logPath, selectFile: true);
            }

            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine("已从系统中卸载 QingSnap。");
        builder.AppendLine();
        builder.AppendLine($"程序文件夹未被自动删除：{executableDirectory}");
        if (result.Deleted.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"已删除 {result.Deleted.Count} 项数据。");
        }

        if (result.LogsScheduled)
        {
            builder.AppendLine();
            builder.AppendLine("残留日志已标记为重启或下次登录后删除。");
        }

        builder.AppendLine();
        builder.Append("是否打开程序文件夹？");
        var openFolder = MessageBox.Show(
            builder.ToString(),
            "QingSnap 已卸载",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (openFolder == MessageBoxResult.Yes)
        {
            OpenInExplorer(executableDirectory, selectFile: false);
        }
    }

    private static void OpenInExplorer(string path, bool selectFile)
    {
        try
        {
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(selectFile ? $"/select,{path}" : path);
            Process.Start(startInfo);
        }
        catch (Exception)
        {
        }
    }
}
