namespace QingSnap.App.Models;

public sealed record CaptureResult(
    CaptureRegion Region,
    string ImagePath,
    int ImageWidth,
    int ImageHeight,
    bool CopyRequested,
    bool CopiedToClipboard,
    IReadOnlyList<string>? ImagePaths = null)
{
    public IReadOnlyList<string> SavedImagePaths => ImagePaths ?? [ImagePath];

    public int ImageCount => SavedImagePaths.Count;
}
