using QingSnap.App.Models;

namespace QingSnap.App.Services;

internal readonly record struct MatrixParameters(int Rows, int Columns, int GapX, int GapY);

internal readonly record struct MatrixCellRegion(MatrixCell Cell, CaptureRegion Region);

internal static class MatrixLayout
{
    public const int MaxRows = 50;
    public const int MaxColumns = 50;
    public const int DefaultRows = 3;
    public const int DefaultColumns = 3;
    public const int DefaultGap = 8;

    public static MatrixParameters Normalize(
        CaptureRegion bounds,
        int rows,
        int columns,
        int gapX,
        int gapY)
    {
        var maxRows = bounds.Height > 0 ? Math.Min(MaxRows, bounds.Height) : MaxRows;
        var maxColumns = bounds.Width > 0 ? Math.Min(MaxColumns, bounds.Width) : MaxColumns;
        rows = Math.Clamp(rows, 1, Math.Max(1, maxRows));
        columns = Math.Clamp(columns, 1, Math.Max(1, maxColumns));

        return new MatrixParameters(
            rows,
            columns,
            Math.Clamp(gapX, 0, MaxGap(bounds.Width, columns)),
            Math.Clamp(gapY, 0, MaxGap(bounds.Height, rows)));
    }

    public static int MaxGap(int available, int count)
    {
        if (available <= 0)
        {
            return 0;
        }

        return count <= 1 ? available : Math.Max(0, (available - count) / (count - 1));
    }

    public static IReadOnlyList<CaptureRegion> Calculate(
        CaptureRegion bounds,
        int rows,
        int columns,
        int gapX,
        int gapY,
        IReadOnlyList<MatrixCell>? excluded = null) =>
        CalculateCells(bounds, rows, columns, gapX, gapY, excluded)
            .Select(cell => cell.Region)
            .ToArray();

    public static IReadOnlyList<CaptureRegion> Calculate(MatrixSpec spec) =>
        Calculate(spec.Bounds, spec.Rows, spec.Columns, spec.GapX, spec.GapY, spec.ExcludedCells);

    public static IReadOnlyList<MatrixCellRegion> CalculateCells(
        CaptureRegion bounds,
        int rows,
        int columns,
        int gapX,
        int gapY,
        IReadOnlyList<MatrixCell>? excluded = null)
    {
        if (!bounds.IsValid || rows < 1 || columns < 1 || gapX < 0 || gapY < 0)
        {
            return [];
        }

        var usableWidth = bounds.Width - ((columns - 1) * gapX);
        var usableHeight = bounds.Height - ((rows - 1) * gapY);
        if (usableWidth < columns || usableHeight < rows)
        {
            return [];
        }

        var cellWidth = usableWidth / columns;
        var remainingWidth = usableWidth % columns;
        var cellHeight = usableHeight / rows;
        var remainingHeight = usableHeight % rows;

        var cells = new List<MatrixCellRegion>(rows * columns);
        for (var row = 1; row <= rows; row++)
        {
            var y = bounds.Y
                    + ((row - 1) * (cellHeight + gapY))
                    + Math.Min(row - 1, remainingHeight);
            var height = cellHeight + (row - 1 < remainingHeight ? 1 : 0);
            for (var column = 1; column <= columns; column++)
            {
                var cell = new MatrixCell(row, column);
                if (IsExcluded(excluded, cell))
                {
                    continue;
                }

                var x = bounds.X
                        + ((column - 1) * (cellWidth + gapX))
                        + Math.Min(column - 1, remainingWidth);
                var width = cellWidth + (column - 1 < remainingWidth ? 1 : 0);
                cells.Add(new MatrixCellRegion(cell, new CaptureRegion(x, y, width, height)));
            }
        }

        return cells;
    }

    public static bool IsExcluded(IReadOnlyList<MatrixCell>? excluded, MatrixCell cell) =>
        excluded is { Count: > 0 } && excluded.Contains(cell);

    public static bool IsExcluded(IReadOnlyList<MatrixCell>? excluded, int row, int column) =>
        IsExcluded(excluded, new MatrixCell(row, column));
}
