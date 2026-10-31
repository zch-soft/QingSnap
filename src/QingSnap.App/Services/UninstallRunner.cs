using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
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
        return result with { Errors = errors.Concat(result.Errors).ToArray() };
    }

    public static void ShowSummary(UninstallResult result)
    {
        var executableDirectory = Path.GetDirectoryName(
            Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
        var builder = new StringBuilder();
        builder.AppendLine("已从系统中卸载 QingSnap。");
        builder.AppendLine();
        builder.AppendLine($"程序文件夹未被自动删除：{executableDirectory}");
        if (result.Deleted.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"已删除 {result.Deleted.Count} 项数据。");
        }

        if (result.Errors.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("以下项目未能完成：");
            foreach (var error in result.Errors)
            {
                builder.AppendLine($"· {error}");
            }
        }

        builder.AppendLine();
        builder.Append("是否打开程序文件夹？");
        var openFolder = MessageBox.Show(
            builder.ToString(),
            "QingSnap 已卸载",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (openFolder != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{executableDirectory}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
        }
    }
}
