using System.IO;
using QingSnap.App.Infrastructure;

namespace QingSnap.App.Services;

public sealed class ShortcutService
{
    public ShortcutService(string? shortcutPath = null)
    {
        ShortcutPath = string.IsNullOrWhiteSpace(shortcutPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "QingSnap.lnk")
            : Path.GetFullPath(shortcutPath);
    }

    public string ShortcutPath { get; }

    public bool Exists() => File.Exists(ShortcutPath);

    public void Create()
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定 QingSnap 程序路径。");
        var directory = Path.GetDirectoryName(ShortcutPath)
            ?? throw new InvalidOperationException("开始菜单目录无效。");
        Directory.CreateDirectory(directory);
        ShellLinkInterop.Create(
            ShortcutPath,
            executablePath,
            Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            executablePath,
            "QingSnap 截图工具");
        if (!File.Exists(ShortcutPath))
        {
            throw new IOException("开始菜单快捷方式创建失败。");
        }
    }

    public void Remove()
    {
        if (File.Exists(ShortcutPath))
        {
            File.Delete(ShortcutPath);
        }
    }
}
