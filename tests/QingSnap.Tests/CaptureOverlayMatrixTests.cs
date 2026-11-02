using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingSnap.App.Models;
using QingSnap.App.Services;
using QingSnap.App.Views;
using Xunit;
using DrawingRectangle = System.Drawing.Rectangle;
using SelectionMode = QingSnap.App.Models.SelectionMode;

namespace QingSnap.Tests;

public sealed class CaptureOverlayMatrixTests
{
    [Fact]
    public void MatrixSelectionSetRestoresCellsAndProducesMatrixOutput()
    {
        var (matrix, free, global) = RunInSta(() =>
        {
            EnsureApplicationResources();
            var snapshot = new ScreenSnapshot(
                new DrawingRectangle(100, 50, 300, 300),
                CreateImage(300, 300));
            var set = new SelectionSet(
                SelectionMode.Matrix,
                [],
                new MatrixSpec(
                    new CaptureRegion(0, 0, 300, 300),
                    2,
                    2,
                    10,
                    10,
                    [new MatrixCell(1, 2)]),
                Padding: 3);
            var window = new CaptureOverlayWindow(
                snapshot,
                showActionToolbar: true,
                settings: new AppSettings());
            try
            {
                window.Width = 300;
                window.Height = 300;
                window.Show();
                window.UpdateLayout();
                window.LoadSelectionSet(set, useSetPadding: true);
                window.UpdateLayout();
                Assert.True(window.IsVisible, "覆盖层窗口未能显示，无法验证矩阵输出。");
                var matrixOutput = window.CreateSelectionOutput();
                var globalSelection = window.CreateGlobalSelectionSet();

                window.LoadSelectionSet(new SelectionSet(
                    SelectionMode.Free,
                    [new CaptureRegion(10, 10, 100, 60), new CaptureRegion(150, 150, 80, 40)]));
                window.UpdateLayout();
                return (matrixOutput, window.CreateSelectionOutput(), globalSelection);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.NotNull(matrix);
        Assert.Equal(3, matrix!.Images.Count);
        Assert.Equal(SelectionMode.Matrix, matrix.Mode);
        Assert.NotNull(matrix.Matrix);
        Assert.Equal(2, matrix.Matrix!.Rows);
        Assert.Equal(2, matrix.Matrix.Columns);
        Assert.Equal(10, matrix.Matrix.GapX);
        Assert.NotNull(matrix.Cells);
        Assert.Equal([new MatrixCell(1, 1), new MatrixCell(2, 1), new MatrixCell(2, 2)], matrix.Cells!);
        Assert.All(matrix.Images, image =>
        {
            Assert.Equal(145, image.PixelWidth);
            Assert.Equal(145, image.PixelHeight);
        });
        Assert.Equal(
            ["QingSnap_1_1.png", "QingSnap_2_1.png", "QingSnap_2_2.png"],
            Enumerable.Range(0, matrix.Images.Count).Select(index => SelectionOutputNaming.MultiFileName(
                "QingSnap",
                index,
                matrix.Images.Count,
                matrix.Matrix,
                matrix.Cells,
                ".png")));

        Assert.NotNull(global);
        Assert.Equal(SelectionMode.Matrix, global!.Mode);
        Assert.Equal(new CaptureRegion(100, 50, 300, 300), global.Matrix!.Bounds);
        Assert.Equal(3, global.Padding);
        Assert.Contains(new MatrixCell(1, 2), global.Matrix.ExcludedCells!);
        Assert.Equal(3, global.Regions.Count);

        Assert.NotNull(free);
        Assert.Equal(2, free!.Images.Count);
        Assert.Equal(SelectionMode.Free, free.Mode);
        Assert.Null(free.Matrix);
        Assert.Null(free.Cells);
        Assert.Equal(
            ["QingSnap_1.png", "QingSnap_2.png"],
            Enumerable.Range(0, free.Images.Count).Select(index => SelectionOutputNaming.MultiFileName(
                "QingSnap", index, free.Images.Count, free.Matrix, free.Cells, ".png")));
    }

    [Fact]
    public void SmartPreviewKeepsShadeAndToolbarWhileSelectionExists()
    {
        _ = RunInSta(() =>
        {
            EnsureApplicationResources();
            var snapshot = new ScreenSnapshot(
                new DrawingRectangle(100, 50, 300, 300),
                CreateImage(300, 300));
            var window = new CaptureOverlayWindow(
                snapshot,
                showActionToolbar: true,
                settings: new AppSettings());
            try
            {
                window.Width = 300;
                window.Height = 300;
                window.Show();
                window.UpdateLayout();
                window.LoadSelectionSet(SelectionSet.Single(new CaptureRegion(10, 10, 100, 60)));
                window.UpdateLayout();

                var showSmartPreview = typeof(CaptureOverlayWindow).GetMethod(
                    "ShowSmartPreview",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(showSmartPreview);
                var scale = window.Surface.ActualWidth / 300.0;
                Assert.True(scale > 0, "覆盖层画布尺寸无效。");
                showSmartPreview.Invoke(window, [new Rect(150 * scale, 150 * scale, 120 * scale, 100 * scale)]);
                window.UpdateLayout();

                var mask = window.ShadeMask.Data;
                Assert.False(mask.FillContains(new Point(50 * scale, 50 * scale)), "选区内应保持明亮。");
                Assert.True(mask.FillContains(new Point(5 * scale, 5 * scale)), "选区外的蒙版不应被窗口预览擦掉。");
                Assert.True(mask.FillContains(new Point(200 * scale, 200 * scale)), "预览区域不应在已有选区时挖洞。");
                Assert.Equal(Visibility.Visible, window.CaptureToolbar.Visibility);
                Assert.Equal(70 * scale + 6, System.Windows.Controls.Canvas.GetTop(window.CaptureToolbar), 3);
            }
            finally
            {
                window.Close();
            }

            return true;
        });
    }

    private static T RunInSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        if (failure is not null)
        {
            throw new InvalidOperationException("覆盖层矩阵集成测试失败。", failure);
        }

        Assert.False(thread.IsAlive, "覆盖层测试线程未在 30 秒内结束。");
        return result;
    }

    private static void EnsureApplicationResources()
    {
        if (System.Windows.Application.Current is null)
        {
            var application = new QingSnap.App.App
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            application.InitializeComponent();
        }
    }

    private static BitmapSource CreateImage(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                pixels[offset] = (byte)(x % 256);
                pixels[offset + 1] = (byte)(y % 256);
                pixels[offset + 2] = 0x40;
                pixels[offset + 3] = 0xFF;
            }
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }
}
