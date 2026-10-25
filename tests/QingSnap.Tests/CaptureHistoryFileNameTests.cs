using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class CaptureHistoryFileNameTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "QingSnap-history-name-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveWithFileNameUsesTheRequestedDirectoryAndName()
    {
        using var history = CreateHistoryService();
        var path = history.SaveWithFileName(
            CreateSolidImage(),
            Path.Combine("2026-10", "group-1"),
            "QingSnap_1_1.png",
            null);

        Assert.True(File.Exists(path));
        Assert.Equal("QingSnap_1_1.png", Path.GetFileName(path));
        Assert.Equal(
            Path.Combine(history.HistoryDirectory, "2026-10", "group-1"),
            Path.GetDirectoryName(path));
    }

    [Fact]
    public void SaveWithFileNameNormalizesTheExtensionToTheConfiguredFormat()
    {
        using var history = CreateHistoryService();
        var path = history.SaveWithFileName(
            CreateSolidImage(),
            "group-2",
            "QingSnap_1_1.PNG",
            null);

        Assert.Equal("QingSnap_1_1.png", Path.GetFileName(path));
    }

    [Fact]
    public void SaveWithFileNameRejectsDirectoriesOutsideTheHistoryRoot()
    {
        using var history = CreateHistoryService();

        Assert.Throws<InvalidOperationException>(() => history.SaveWithFileName(
            CreateSolidImage(),
            Path.Combine("..", "outside"),
            "QingSnap_1_1.png",
            null));
    }

    private CaptureHistoryService CreateHistoryService()
    {
        var settings = new AppSettingsService(_directory);
        return new CaptureHistoryService(settings);
    }

    private static BitmapSource CreateSolidImage()
    {
        var pixels = Enumerable.Repeat(new byte[] { 10, 20, 200, 255 }, 4)
            .SelectMany(pixel => pixel)
            .ToArray();
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        image.Freeze();
        return image;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
