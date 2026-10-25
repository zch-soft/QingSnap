using System.Windows.Media.Imaging;

namespace QingSnap.App.Models;

public sealed record CaptureSelectionOutput(
    IReadOnlyList<BitmapSource> Images,
    IReadOnlyList<CaptureRegion> Regions,
    BitmapSource? CompositeImage,
    CaptureRegion? CompositeRegion,
    int Padding = 0,
    SelectionMode Mode = SelectionMode.Free,
    MatrixSpec? Matrix = null,
    IReadOnlyList<MatrixCell>? Cells = null)
{
    public bool IsSingle => Images.Count == 1;

    public bool HasTransparency => Images.Count > 1 || Padding > 0;

    public bool IsMatrix => Mode == SelectionMode.Matrix && Matrix is not null;

    public BitmapSource PrimaryImage =>
        Images.Count > 0
            ? Images[0]
            : throw new InvalidOperationException("截图输出中没有可用的图片。");

    public CaptureRegion PrimaryRegion =>
        Regions.Count > 0
            ? Regions[0]
            : throw new InvalidOperationException("截图输出中没有可用的选区。");
}
