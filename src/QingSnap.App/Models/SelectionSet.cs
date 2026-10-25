namespace QingSnap.App.Models;

public enum SelectionMode
{
    Free,
    Matrix
}

public readonly record struct MatrixCell(int Row, int Column);

public sealed record MatrixSpec(
    CaptureRegion Bounds,
    int Rows,
    int Columns,
    int GapX,
    int GapY,
    IReadOnlyList<MatrixCell>? ExcludedCells = null)
{
    public bool IsValid =>
        Bounds.IsValid &&
        Rows >= 1 &&
        Columns >= 1 &&
        GapX >= 0 &&
        GapY >= 0;
}

public sealed record SelectionSet(
    SelectionMode Mode,
    IReadOnlyList<CaptureRegion> Regions,
    MatrixSpec? Matrix = null,
    int Padding = 0)
{
    public static SelectionSet Single(CaptureRegion region) =>
        new(SelectionMode.Free, [region]);

    public bool IsSingle => Regions.Count == 1;

    public bool HasSelection => Regions.Any(region => region.IsValid);
}
