using System.Diagnostics;
using System.IO;
using System.Text;

namespace QingSnap.App.Infrastructure;

internal static class PostExitCleanup
{
    public static bool Schedule(IEnumerable<string> paths)
    {
        var targets = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targets.Length == 0)
        {
            return true;
        }

        try
        {
            var script = new StringBuilder();
            script.AppendLine($"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue");
            foreach (var target in targets)
            {
                script.AppendLine(
                    $"Remove-Item -LiteralPath '{target.Replace("'", "''")}' -Recurse -Force -ErrorAction SilentlyContinue");
            }

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var startInfo = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-WindowStyle");
            startInfo.ArgumentList.Add("Hidden");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(encoded);
            Process.Start(startInfo);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
