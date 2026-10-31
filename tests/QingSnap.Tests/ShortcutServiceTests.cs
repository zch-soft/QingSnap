using System.IO;
using System.Threading;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class ShortcutServiceTests : IDisposable
{
    private readonly string _directory;

    public ShortcutServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"QingSnap-shortcut-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void CreateAndRemove_ManagesShortcutFile()
    {
        var shortcutPath = Path.Combine(_directory, "QingSnap.lnk");
        var service = new ShortcutService(shortcutPath);
        Assert.False(service.Exists());

        RunOnStaThread(service.Create);
        Assert.True(service.Exists());

        RunOnStaThread(service.Remove);
        Assert.False(service.Exists());
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("STA action failed.", failure);
        }
    }
}
