using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class MatrixLayoutTests
{
    [Fact]
    public void EvenDivisionProducesEqualCells()
    {
        var cells = MatrixLayout.Calculate(new CaptureRegion(0, 0, 300, 300), 3, 3, 0, 0);

        Assert.Equal(9, cells.Count);
        Assert.All(cells, cell => Assert.Equal(new CaptureRegion(cell.X, cell.Y, 100, 100), cell));
        Assert.Equal(new CaptureRegion(0, 0, 100, 100), cells[0]);
        Assert.Equal(new CaptureRegion(100, 100, 100, 100), cells[4]);
        Assert.Equal(new CaptureRegion(200, 200, 100, 100), cells[8]);
    }

    [Fact]
    public void RemainderIsAssignedToLeadingCellsSoTotalWidthMatchesBounds()
    {
        var cells = MatrixLayout.Calculate(new CaptureRegion(10, 20, 300, 300), 3, 3, 10, 10);

        Assert.Equal(9, cells.Count);
        Assert.Equal([94, 93, 93], cells.Take(3).Select(cell => cell.Width));
        Assert.Equal([10, 114, 217], cells.Take(3).Select(cell => cell.X));
        Assert.Equal(10 + 300, cells[2].X + cells[2].Width);
        Assert.Equal([94, 93, 93], new[] { cells[0].Height, cells[3].Height, cells[6].Height });
        Assert.Equal([20, 124, 227], new[] { cells[0].Y, cells[3].Y, cells[6].Y });
        Assert.Equal(20 + 300, cells[6].Y + cells[6].Height);
    }

    [Fact]
    public void SingleRowAndSingleColumnLayoutsWork()
    {
        var row = MatrixLayout.Calculate(new CaptureRegion(0, 0, 100, 30), 1, 3, 5, 0);
        Assert.Equal([0, 35, 70], row.Select(cell => cell.X));
        Assert.Equal([30, 30, 30], row.Select(cell => cell.Width));
        Assert.All(row, cell => Assert.Equal(30, cell.Height));

        var column = MatrixLayout.Calculate(new CaptureRegion(0, 0, 40, 90), 3, 1, 0, 5);
        Assert.Equal([0, 32, 64], column.Select(cell => cell.Y));
        Assert.Equal([27, 27, 26], column.Select(cell => cell.Height));
        Assert.Equal([40, 40, 40], column.Select(cell => cell.Width));
    }

    [Fact]
    public void ExcludedCellsKeepTheirOriginalCoordinatesAndRowColumnNumbers()
    {
        var excluded = new[] { new MatrixCell(1, 1), new MatrixCell(2, 2) };
        var cells = MatrixLayout.CalculateCells(
            new CaptureRegion(0, 0, 200, 200),
            2,
            2,
            0,
            0,
            excluded);

        Assert.Equal(2, cells.Count);
        Assert.Equal(new MatrixCell(1, 2), cells[0].Cell);
        Assert.Equal(new CaptureRegion(100, 0, 100, 100), cells[0].Region);
        Assert.Equal(new MatrixCell(2, 1), cells[1].Cell);
        Assert.Equal(new CaptureRegion(0, 100, 100, 100), cells[1].Region);
    }

    [Fact]
    public void MissingSpaceProducesNoCells()
    {
        Assert.Empty(MatrixLayout.Calculate(new CaptureRegion(0, 0, 2, 100), 3, 3, 0, 0));
        Assert.Empty(MatrixLayout.Calculate(new CaptureRegion(0, 0, 100, 100), 0, 3, 0, 0));
        Assert.Empty(MatrixLayout.Calculate(new CaptureRegion(0, 0, 0, 100), 3, 3, 0, 0));
    }

    [Fact]
    public void NormalizeClampsRowsColumnsAndGaps()
    {
        var bounds = new CaptureRegion(0, 0, 300, 300);

        Assert.Equal(new MatrixParameters(1, 1, 0, 0), MatrixLayout.Normalize(bounds, 0, 0, -5, -5));
        Assert.Equal(new MatrixParameters(50, 50, 5, 5), MatrixLayout.Normalize(bounds, 99, 99, 5, 5));
        Assert.Equal(new MatrixParameters(3, 3, 148, 148), MatrixLayout.Normalize(bounds, 3, 3, 500, 500));

        var narrow = MatrixLayout.Normalize(new CaptureRegion(0, 0, 4, 7), 9, 9, 0, 0);
        Assert.Equal(7, narrow.Rows);
        Assert.Equal(4, narrow.Columns);
    }

    [Fact]
    public void NormalizedParametersAlwaysProduceCells()
    {
        var bounds = new CaptureRegion(5, 5, 137, 61);
        var parameters = MatrixLayout.Normalize(bounds, 50, 50, 999, 999);
        var cells = MatrixLayout.Calculate(
            bounds,
            parameters.Rows,
            parameters.Columns,
            parameters.GapX,
            parameters.GapY);

        Assert.Equal(parameters.Rows * parameters.Columns, cells.Count);
        Assert.All(cells, cell => Assert.True(cell.Width >= 1 && cell.Height >= 1));
        Assert.All(cells, cell =>
        {
            Assert.True(cell.X >= bounds.X);
            Assert.True(cell.Y >= bounds.Y);
            Assert.True(cell.X + cell.Width <= bounds.X + bounds.Width);
            Assert.True(cell.Y + cell.Height <= bounds.Y + bounds.Height);
        });
    }
}
