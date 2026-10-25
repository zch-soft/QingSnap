using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class ClipboardImageFormatsTests
{
    [Fact]
    public void DibUsesBottomUpRowsAndAStandardHeader()
    {
        // Row 0 (top): red, green; row 1 (bottom): blue, white.
        byte[] pixels =
        [
            0, 0, 255, 255, 0, 255, 0, 255,
            255, 0, 0, 255, 255, 255, 255, 255
        ];
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);

        var dib = ClipboardImageFormats.BuildDib(image);

        Assert.Equal(40 + 16, dib.Length);
        Assert.Equal(40, BitConverter.ToInt32(dib, 0));
        Assert.Equal(2, BitConverter.ToInt32(dib, 4));
        Assert.Equal(2, BitConverter.ToInt32(dib, 8));
        Assert.Equal(1, BitConverter.ToInt16(dib, 12));
        Assert.Equal(32, BitConverter.ToInt16(dib, 14));
        Assert.Equal(16, BitConverter.ToInt32(dib, 20));

        Assert.Equal([255, 0, 0, 255, 255, 255, 255, 255], dib[40..48]);
        Assert.Equal([0, 0, 255, 255, 0, 255, 0, 255], dib[48..56]);
    }

    [Fact]
    public void FileDropUsesUnicodeNamesAndDoubleNullTerminator()
    {
        string[] paths = [@"C:\temp\QingSnap_1_1.png", @"C:\temp\QingSnap_1_2.png"];

        var drop = ClipboardImageFormats.BuildFileDrop(paths);

        Assert.Equal(20, BitConverter.ToInt32(drop, 0));
        Assert.Equal(0, BitConverter.ToInt32(drop, 12));
        Assert.Equal(1, BitConverter.ToInt32(drop, 16));
        var names = Encoding.Unicode.GetString(drop, 20, drop.Length - 20);
        Assert.Equal(string.Join("\0", paths) + "\0\0", names);
    }

    [Fact]
    public void PngEncodingProducesADecodableImage()
    {
        var image = CreateSolidImage(3, 2);
        var png = ClipboardImageFormats.EncodePng(image);

        using var stream = new System.IO.MemoryStream(png);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        Assert.Equal(3, decoder.Frames[0].PixelWidth);
        Assert.Equal(2, decoder.Frames[0].PixelHeight);
    }

    private static BitmapSource CreateSolidImage(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)0x80);
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    }
}
