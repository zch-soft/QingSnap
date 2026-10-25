using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class SelectionImageComposerTests
{
    [Fact]
    public void BoundingBoxExpandsByPaddingAndClampsToTheScreen()
    {
        CaptureRegion[] regions =
        [
            new(2, 2, 2, 2),
            new(6, 6, 2, 2)
        ];

        Assert.Equal(new CaptureRegion(1, 1, 8, 8), SelectionImageComposer.GetBoundingBox(
            regions,
            1,
            new System.Drawing.Rectangle(0, 0, 10, 10)));

        Assert.Equal(new CaptureRegion(0, 0, 10, 10), SelectionImageComposer.GetBoundingBox(
            regions,
            20,
            new System.Drawing.Rectangle(0, 0, 10, 10)));
    }

    [Fact]
    public void BoundingBoxIsEmptyWhenThereAreNoRegions()
    {
        var box = SelectionImageComposer.GetBoundingBox(
            [],
            4,
            new System.Drawing.Rectangle(0, 0, 10, 10));

        Assert.False(box.IsValid);
    }

    [Fact]
    public void CompositeKeepsSelectedPixelsAndTurnsEverythingElseTransparent()
    {
        var source = CreateSource(10, 10);
        CaptureRegion[] regions =
        [
            new(2, 2, 2, 2),
            new(6, 6, 2, 2)
        ];

        var composite = SelectionImageComposer.CreateTransparentComposite(source, regions, 0);

        Assert.Equal(6, composite.PixelWidth);
        Assert.Equal(6, composite.PixelHeight);
        Assert.Equal(255, AlphaAt(composite, 0, 0));
        Assert.Equal(255, AlphaAt(composite, 1, 1));
        Assert.Equal(0, AlphaAt(composite, 2, 2));
        Assert.Equal(0, AlphaAt(composite, 3, 3));
        Assert.Equal(255, AlphaAt(composite, 4, 4));
        Assert.Equal(255, AlphaAt(composite, 5, 5));
        Assert.Equal(PixelAt(source, 2, 2), PixelAt(composite, 0, 0));
    }

    [Fact]
    public void CompositePaddingAreaIsTransparent()
    {
        var source = CreateSource(10, 10);
        CaptureRegion[] regions =
        [
            new(2, 2, 2, 2),
            new(6, 6, 2, 2)
        ];

        var composite = SelectionImageComposer.CreateTransparentComposite(source, regions, 1);

        Assert.Equal(8, composite.PixelWidth);
        Assert.Equal(8, composite.PixelHeight);
        Assert.Equal(0, AlphaAt(composite, 0, 0));
        Assert.Equal(255, AlphaAt(composite, 1, 1));
        Assert.Equal(0, AlphaAt(composite, 7, 7));
    }

    [Fact]
    public void OverlappingRegionsRemainOpaque()
    {
        var source = CreateSource(8, 8);
        CaptureRegion[] regions =
        [
            new(0, 0, 4, 4),
            new(2, 2, 4, 4)
        ];

        var composite = SelectionImageComposer.CreateTransparentComposite(source, regions, 0);

        Assert.Equal(6, composite.PixelWidth);
        Assert.Equal(6, composite.PixelHeight);
        Assert.Equal(255, AlphaAt(composite, 3, 3));
    }

    [Fact]
    public void TransparentPaddingPreservesTheOriginalPixels()
    {
        var source = CreateSource(2, 2);
        var padded = SelectionImageComposer.AddTransparentPadding(source, 1);

        Assert.Equal(4, padded.PixelWidth);
        Assert.Equal(4, padded.PixelHeight);
        Assert.Equal(0, AlphaAt(padded, 0, 0));
        Assert.Equal(0, AlphaAt(padded, 3, 3));
        Assert.Equal(255, AlphaAt(padded, 1, 1));
        Assert.Equal(PixelAt(source, 0, 0), PixelAt(padded, 1, 1));
    }

    [Fact]
    public void SingleRegionCompositeMatchesTheRegionSize()
    {
        var source = CreateSource(10, 10);
        var composite = SelectionImageComposer.CreateTransparentComposite(
            source,
            [new CaptureRegion(2, 3, 4, 5)],
            0);

        Assert.Equal(4, composite.PixelWidth);
        Assert.Equal(5, composite.PixelHeight);
        Assert.All(
            Enumerable.Range(0, 4).SelectMany(x => Enumerable.Range(0, 5).Select(y => (x, y))),
            point => Assert.Equal(255, AlphaAt(composite, point.x, point.y)));
    }

    private static BitmapSource CreateSource(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * stride) + (x * 4);
                pixels[index] = (byte)(x * 10);
                pixels[index + 1] = (byte)(y * 10);
                pixels[index + 2] = 200;
                pixels[index + 3] = 255;
            }
        }

        var source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32,
            null,
            pixels,
            stride);
        source.Freeze();
        return source;
    }

    private static byte AlphaAt(BitmapSource source, int x, int y) => PixelAt(source, x, y).A;

    private static (byte B, byte G, byte R, byte A) PixelAt(BitmapSource source, int x, int y)
    {
        var pixel = new byte[4];
        source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[0], pixel[1], pixel[2], pixel[3]);
    }
}
