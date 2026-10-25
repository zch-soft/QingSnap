using System.IO;
using System.Windows.Media.Imaging;

namespace QingSnap.App.Services;

internal sealed class ClipboardTempFileStore
{
    public const int RetentionHours = 24;
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(RetentionHours);

    public ClipboardTempFileStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Path.GetTempPath(),
            "QingSnap",
            "Clipboard");
    }

    public string RootDirectory { get; }

    public string CreateSessionDirectory()
    {
        Directory.CreateDirectory(RootDirectory);
        var directory = Path.Combine(RootDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public string WritePng(string directory, string fileName, BitmapSource image)
    {
        var path = Path.Combine(directory, fileName);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    public int CleanupExpired() => CleanupExpired(RootDirectory, DefaultRetention, DateTime.UtcNow);

    public static int CleanupExpired(string rootDirectory, TimeSpan maxAge, DateTime utcNow)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return 0;
        }

        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(rootDirectory))
        {
            try
            {
                var info = new DirectoryInfo(directory);
                if (utcNow - info.LastWriteTimeUtc <= maxAge)
                {
                    continue;
                }

                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warning(
                    "Clipboard",
                    $"清理临时剪贴板目录失败 {Path.GetFileName(directory)}：{exception.Message}");
            }
        }

        return removed;
    }
}
