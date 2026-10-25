namespace QingSnap.App.Models;

public sealed record SelectionGroupManifest(
    int Version,
    string GroupId,
    SelectionMode Mode,
    int Rows,
    int Columns,
    int Count,
    DateTimeOffset CreatedAt)
{
    public const int CurrentVersion = 1;

    public string DisplayLabel => Mode == SelectionMode.Matrix && Rows > 0 && Columns > 0
        ? $"矩阵 {Rows}×{Columns} · {Count} 张"
        : $"多图 · {Count} 张";
}
