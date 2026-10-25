using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingSnap.App.Services;

internal static class ClipboardImageFormats
{
    private const int DibHeaderSize = 40;

    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static byte[] BuildDib(BitmapSource image)
    {
        BitmapSource source = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        source.CopyPixels(pixels, stride, 0);

        var data = new byte[DibHeaderSize + pixels.Length];
        WriteInt32(data, 0, DibHeaderSize);
        WriteInt32(data, 4, width);
        // Positive height means a bottom-up DIB, which is what CF_DIB consumers expect.
        WriteInt32(data, 8, height);
        WriteInt16(data, 12, 1);
        WriteInt16(data, 14, 32);
        WriteInt32(data, 16, 0);
        WriteInt32(data, 20, pixels.Length);
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(
                pixels,
                row * stride,
                data,
                DibHeaderSize + ((height - 1 - row) * stride),
                stride);
        }

        return data;
    }

    public static byte[] BuildFileDrop(IReadOnlyList<string> paths)
    {
        var list = string.Join("\0", paths) + "\0\0";
        var names = Encoding.Unicode.GetBytes(list);
        var data = new byte[20 + names.Length];
        WriteInt32(data, 0, 20);
        WriteInt32(data, 12, 0);
        WriteInt32(data, 16, 1);
        names.CopyTo(data, 20);
        return data;
    }

    private static void WriteInt32(byte[] target, int offset, int value) =>
        BitConverter.TryWriteBytes(target.AsSpan(offset, sizeof(int)), value);

    private static void WriteInt16(byte[] target, int offset, short value) =>
        BitConverter.TryWriteBytes(target.AsSpan(offset, sizeof(short)), value);
}
