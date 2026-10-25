using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class CaptureSetClipboardFormatterTests
{
    [Fact]
    public void MatrixGridKeepsRowAndColumnPositions()
    {
        var matrix = new MatrixSpec(new CaptureRegion(0, 0, 300, 200), 3, 2, 4, 4);
        MatrixCell[] cells = [new(1, 2), new(3, 1)];

        var (rows, columns, positions) = CaptureSetClipboardFormatter.BuildGrid(
            SelectionMode.Matrix,
            2,
            matrix,
            cells,
            "Auto");

        Assert.Equal(3, rows);
        Assert.Equal(2, columns);
        Assert.Equal(new ClipboardGridPosition(1, 2), positions[0]);
        Assert.Equal(new ClipboardGridPosition(3, 1), positions[1]);
    }

    [Fact]
    public void FreeSelectionsDefaultToAColumnAndCanOptIntoARow()
    {
        var column = CaptureSetClipboardFormatter.BuildGrid(SelectionMode.Free, 3, null, null, "Auto");
        Assert.Equal((3, 1), (column.Rows, column.Columns));
        Assert.Equal(new ClipboardGridPosition(2, 1), column.Positions[1]);

        var row = CaptureSetClipboardFormatter.BuildGrid(SelectionMode.Free, 3, null, null, "Row");
        Assert.Equal((1, 3), (row.Rows, row.Columns));
        Assert.Equal(new ClipboardGridPosition(1, 3), row.Positions[2]);

        var explicitColumn = CaptureSetClipboardFormatter.BuildGrid(SelectionMode.Free, 2, null, null, "Column");
        Assert.Equal((2, 1), (explicitColumn.Rows, explicitColumn.Columns));
    }

    [Fact]
    public void DocumentJsonCarriesModeCellsAndPaths()
    {
        var request = new CaptureSetClipboardRequest(
            [CreateImage(), CreateImage()],
            [new CaptureRegion(10, 20, 30, 40), new CaptureRegion(60, 70, 80, 90)],
            SelectionMode.Matrix,
            new MatrixSpec(new CaptureRegion(0, 0, 300, 200), 2, 2, 6, 6),
            [new MatrixCell(1, 1), new MatrixCell(2, 2)],
            new CaptureRegion(0, 0, 300, 200),
            "Auto",
            "Shot",
            3,
            IncludeFileDrop: true);

        var json = CaptureSetClipboardFormatter.BuildDocumentJson(
            request,
            [@"C:\temp\a\Shot_1_1.png", @"C:\temp\a\Shot_2_2.png"],
            2,
            2,
            new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
        var document = JsonSerializer.Deserialize<CaptureSetDocument>(json);

        Assert.NotNull(document);
        Assert.Equal(CaptureSetClipboardFormatter.DocumentVersion, document!.Version);
        Assert.Equal("Matrix", document.Mode);
        Assert.Equal(2, document.Rows);
        Assert.Equal(2, document.Columns);
        Assert.Equal(6, document.GapX);
        Assert.Equal(3, document.Padding);
        Assert.Equal(2, document.Cells.Count);
        Assert.Equal(1, document.Cells[0].Row);
        Assert.Equal(2, document.Cells[1].Column);
        Assert.Equal(@"C:\temp\a\Shot_2_2.png", document.Cells[1].Path);
    }

    private static BitmapSource CreateImage()
    {
        var pixels = new byte[4];
        Array.Fill(pixels, (byte)0x20);
        return BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
    }
}
