using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace QingSnap.App.Services;

public sealed class UninstallRegistryService
{
    internal const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\QingSnap";

    public bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key is not null;
    }

    public void Register()
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定 QingSnap 程序路径。");
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true)
            ?? throw new InvalidOperationException("无法写入卸载登记。");
        key.SetValue("DisplayName", "QingSnap");
        key.SetValue("DisplayVersion", GetDisplayVersion());
        key.SetValue("Publisher", "QingSnap Open Source");
        key.SetValue("DisplayIcon", $"{executablePath},0");
        key.SetValue("InstallLocation", Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory);
        key.SetValue("UninstallString", $"\"{executablePath}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{executablePath}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    public void Remove() => Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);

    private static string GetDisplayVersion()
    {
        var informational = typeof(UninstallRegistryService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Trim();
        if (!string.IsNullOrEmpty(informational))
        {
            return informational.StartsWith('v') ? informational[1..] : informational;
        }

        return typeof(UninstallRegistryService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}
