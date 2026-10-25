using System.Windows.Media.Imaging;

namespace QingSnap.App.Models;

public sealed record CaptureSetClipboardRequest(
    IReadOnlyList<BitmapSource> Images,
    IReadOnlyList<CaptureRegion> Regions,
    SelectionMode Mode = SelectionMode.Free,
    MatrixSpec? Matrix = null,
    IReadOnlyList<MatrixCell>? Cells = null,
    CaptureRegion? PreferredRegion = null,
    string Layout = "Auto",
    string Prefix = "QingSnap",
    int Padding = 0,
    bool IncludeFileDrop = false);

public sealed record CaptureSetClipboardResult(
    string TempDirectory,
    IReadOnlyList<string> FilePaths,
    int Rows,
    int Columns,
    int HtmlByteLength);
