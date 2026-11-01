using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace QingSnap.App.Infrastructure;

internal static class RebootDeletion
{
    private const uint MoveFileDelayUntilReboot = 0x4;
    private const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunOnceValueName = "QingSnapLogCleanup";

    public static bool ScheduleDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return true;
        }

        var scheduled = true;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                scheduled &= TryMoveFileEx(file);
            }

            foreach (var child in Directory
                         .EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
            {
                scheduled &= TryMoveFileEx(child);
            }

            scheduled &= TryMoveFileEx(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            scheduled = false;
        }

        if (!scheduled)
        {
            AddRunOnceFallback(directory);
        }

        return scheduled;
    }

    private static bool TryMoveFileEx(string path)
    {
        try
        {
            return MoveFileEx(path, null, MoveFileDelayUntilReboot);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void AddRunOnceFallback(string directory)
    {
        try
        {
            var script =
                $"Remove-Item -LiteralPath '{directory.Replace("'", "''")}' -Recurse -Force -ErrorAction SilentlyContinue";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            using var key = Registry.CurrentUser.CreateSubKey(RunOnceKeyPath);
            key?.SetValue(
                RunOnceValueName,
                $"\"{powershell}\" -NoProfile -WindowStyle Hidden -EncodedCommand {encoded}");
        }
        catch (Exception)
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, uint flags);
}
