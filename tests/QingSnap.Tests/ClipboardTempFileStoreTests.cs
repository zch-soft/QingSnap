using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class ClipboardTempFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "QingSnap-clipboard-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SessionDirectoryReceivesPngFiles()
    {
        var store = new ClipboardTempFileStore(_root);
        var directory = store.CreateSessionDirectory();
        var path = store.WritePng(directory, "QingSnap_1_1.png", CreateImage());

        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);
    }

    [Fact]
    public void CleanupRemovesOnlyExpiredSessions()
    {
        var store = new ClipboardTempFileStore(_root);
        var fresh = store.CreateSessionDirectory();
        var stale = store.CreateSessionDirectory();
        store.WritePng(stale, "QingSnap_1.png", CreateImage());
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-30));

        var removed = ClipboardTempFileStore.CleanupExpired(
            _root,
            TimeSpan.FromHours(24),
            DateTime.UtcNow);

        Assert.Equal(1, removed);
        Assert.True(Directory.Exists(fresh));
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public void MissingRootIsIgnored()
    {
        Assert.Equal(0, ClipboardTempFileStore.CleanupExpired(
            Path.Combine(_root, "missing"),
            TimeSpan.FromHours(24),
            DateTime.UtcNow));
    }

    private static BitmapSource CreateImage()
    {
        var pixels = new byte[4];
        Array.Fill(pixels, (byte)0x40);
        return BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
        }
    }
}
