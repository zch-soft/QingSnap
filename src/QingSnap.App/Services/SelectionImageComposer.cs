using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using DrawingRectangle = System.Drawing.Rectangle;

namespace QingSnap.App.Services;

internal static class SelectionImageComposer
{
    public static CaptureRegion GetBoundingBox(
        IReadOnlyList<CaptureRegion> regions,
        int padding,
        DrawingRectangle screenBounds)
    {
        var valid = regions.Where(region => region.IsValid).ToArray();
        if (valid.Length == 0 || screenBounds.Width <= 0 || screenBounds.Height <= 0)
        {
            return new CaptureRegion(0, 0, 0, 0);
        }

        var left = valid.Min(region => region.X);
        var top = valid.Min(region => region.Y);
        var right = valid.Max(region => region.X + region.Width);
        var bottom = valid.Max(region => region.Y + region.Height);

        var safePadding = Math.Clamp(padding, 0, 200);
        left = Math.Max(screenBounds.Left, left - safePadding);
        top = Math.Max(screenBounds.Top, top - safePadding);
        right = Math.Min(screenBounds.Right, right + safePadding);
        bottom = Math.Min(screenBounds.Bottom, bottom + safePadding);

        if (right <= left || bottom <= top)
        {
            return new CaptureRegion(0, 0, 0, 0);
        }

        return new CaptureRegion(left, top, right - left, bottom - top);
    }

    public static BitmapSource Crop(BitmapSource source, CaptureRegion region)
    {
        var clipped = ClipToSource(region, source);
        if (!clipped.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "选区不在截图范围内。");
        }

        var cropped = new CroppedBitmap(
            source,
            new Int32Rect(clipped.X, clipped.Y, clipped.Width, clipped.Height));
        cropped.Freeze();
        return cropped;
    }

    public static BitmapSource CreateTransparentComposite(
        BitmapSource source,
        IReadOnlyList<CaptureRegion> regions,
        int padding)
    {
        var bounds = new DrawingRectangle(0, 0, source.PixelWidth, source.PixelHeight);
        var box = GetBoundingBox(regions, padding, bounds);
        if (!box.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(regions), "没有可输出的选区。");
        }

        var formatted = EnsurePbgra32(source);
        var stride = box.Width * 4;
        var destination = new byte[stride * box.Height];
        foreach (var region in regions)
        {
            var clipped = Clip(region, new DrawingRectangle(box.X, box.Y, box.Width, box.Height));
            if (!clipped.IsValid)
            {
                continue;
            }

            var crop = new CroppedBitmap(
                formatted,
                new Int32Rect(clipped.X, clipped.Y, clipped.Width, clipped.Height));
            var cropStride = clipped.Width * 4;
            var cropPixels = new byte[cropStride * clipped.Height];
            crop.CopyPixels(cropPixels, cropStride, 0);
            var destinationX = clipped.X - box.X;
            var destinationY = clipped.Y - box.Y;
            for (var row = 0; row < clipped.Height; row++)
            {
                Buffer.BlockCopy(
                    cropPixels,
                    row * cropStride,
                    destination,
                    ((destinationY + row) * stride) + (destinationX * 4),
                    cropStride);
            }
        }

        var result = BitmapSource.Create(
            box.Width,
            box.Height,
            formatted.DpiX > 0 ? formatted.DpiX : 96,
            formatted.DpiY > 0 ? formatted.DpiY : 96,
            PixelFormats.Pbgra32,
            null,
            destination,
            stride);
        result.Freeze();
        return result;
    }

    public static BitmapSource AddTransparentPadding(BitmapSource image, int padding)
    {
        var safePadding = Math.Clamp(padding, 0, 200);
        if (safePadding == 0)
        {
            return image;
        }

        var formatted = EnsurePbgra32(image);
        var sourceStride = formatted.PixelWidth * 4;
        var sourcePixels = new byte[sourceStride * formatted.PixelHeight];
        formatted.CopyPixels(sourcePixels, sourceStride, 0);

        var width = formatted.PixelWidth + (safePadding * 2);
        var height = formatted.PixelHeight + (safePadding * 2);
        var stride = width * 4;
        var destination = new byte[stride * height];
        for (var row = 0; row < formatted.PixelHeight; row++)
        {
            Buffer.BlockCopy(
                sourcePixels,
                row * sourceStride,
                destination,
                ((row + safePadding) * stride) + (safePadding * 4),
                sourceStride);
        }

        var result = BitmapSource.Create(
            width,
            height,
            formatted.DpiX > 0 ? formatted.DpiX : 96,
            formatted.DpiY > 0 ? formatted.DpiY : 96,
            PixelFormats.Pbgra32,
            null,
            destination,
            stride);
        result.Freeze();
        return result;
    }

    private static BitmapSource EnsurePbgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Pbgra32)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static CaptureRegion ClipToSource(CaptureRegion region, BitmapSource source) =>
        Clip(region, new DrawingRectangle(0, 0, source.PixelWidth, source.PixelHeight));

    private static CaptureRegion Clip(CaptureRegion region, DrawingRectangle bounds)
    {
        var left = Math.Max(bounds.Left, region.X);
        var top = Math.Max(bounds.Top, region.Y);
        var right = Math.Min(bounds.Right, region.X + region.Width);
        var bottom = Math.Min(bounds.Bottom, region.Y + region.Height);
        return right <= left || bottom <= top
            ? new CaptureRegion(0, 0, 0, 0)
            : new CaptureRegion(left, top, right - left, bottom - top);
    }
}
