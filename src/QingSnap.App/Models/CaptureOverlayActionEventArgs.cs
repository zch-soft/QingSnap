using System.Windows.Media.Imaging;
using DrawingRectangle = System.Drawing.Rectangle;

namespace QingSnap.App.Models;

public sealed class CaptureOverlayActionEventArgs : EventArgs
{
    public CaptureOverlayActionEventArgs(
        CaptureOverlayAction action,
        CaptureSelectionOutput output,
        Task<OcrRecognitionResult>? prefetchedOcr = null,
        IReadOnlyList<string>? tags = null)
    {
        Action = action;
        Output = output;
        PrefetchedOcr = prefetchedOcr;
        Tags = tags ?? [];
    }

    public CaptureOverlayAction Action { get; }

    public CaptureSelectionOutput Output { get; }

    public IReadOnlyList<CaptureRegion> Regions => Output.Regions;

    public IReadOnlyList<BitmapSource> Images => Output.Images;

    public BitmapSource? CompositeImage => Output.CompositeImage;

    public CaptureRegion? CompositeRegion => Output.CompositeRegion;

    public DrawingRectangle LocalRegion => Output.PrimaryRegion.ToRectangle();

    public BitmapSource Image => Output.PrimaryImage;

    public Task<OcrRecognitionResult>? PrefetchedOcr { get; }

    public IReadOnlyList<string> Tags { get; }
}
