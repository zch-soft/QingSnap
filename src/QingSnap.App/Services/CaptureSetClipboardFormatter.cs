using System.Text.Json;
using System.Text.Json.Serialization;
using QingSnap.App.Models;
using SelectionMode = QingSnap.App.Models.SelectionMode;

namespace QingSnap.App.Services;

internal readonly record struct ClipboardGridPosition(int Row, int Column);

internal sealed record CaptureSetDocument(
    int Version,
    string Mode,
    int Rows,
    int Columns,
    int GapX,
    int GapY,
    int Padding,
    DateTimeOffset CreatedAt,
    IReadOnlyList<CaptureSetDocumentCell> Cells);

internal sealed record CaptureSetDocumentCell(
    int Row,
    int Column,
    string Path,
    int Width,
    int Height,
    CaptureRegion Region);

internal static class CaptureSetClipboardFormatter
{
    public const string CaptureSetFormatName = "QingSnap.CaptureSet.v2";
    public const int DocumentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static (int Rows, int Columns, IReadOnlyList<ClipboardGridPosition> Positions) BuildGrid(
        SelectionMode mode,
        int count,
        MatrixSpec? matrix,
        IReadOnlyList<MatrixCell>? cells,
        string? layout)
    {
        if (count <= 0)
        {
            return (0, 0, []);
        }

        if (mode == SelectionMode.Matrix && matrix is { IsValid: true } spec)
        {
            var positions = new ClipboardGridPosition[count];
            var rows = spec.Rows;
            var columns = spec.Columns;
            for (var index = 0; index < count; index++)
            {
                var cell = cells is not null && index < cells.Count
                    ? cells[index]
                    : new MatrixCell(1, index + 1);
                var row = Math.Max(1, cell.Row);
                var column = Math.Max(1, cell.Column);
                positions[index] = new ClipboardGridPosition(row, column);
                rows = Math.Max(rows, row);
                columns = Math.Max(columns, column);
            }

            return (rows, columns, positions);
        }

        var freePositions = new ClipboardGridPosition[count];
        if (string.Equals(layout, "Row", StringComparison.OrdinalIgnoreCase))
        {
            for (var index = 0; index < count; index++)
            {
                freePositions[index] = new ClipboardGridPosition(1, index + 1);
            }

            return (1, count, freePositions);
        }

        for (var index = 0; index < count; index++)
        {
            freePositions[index] = new ClipboardGridPosition(index + 1, 1);
        }

        return (count, 1, freePositions);
    }

    public static string BuildDocumentJson(
        CaptureSetClipboardRequest request,
        IReadOnlyList<string> filePaths,
        int rows,
        int columns,
        DateTimeOffset createdAt)
    {
        var positions = BuildGrid(
            request.Mode,
            request.Images.Count,
            request.Matrix,
            request.Cells,
            request.Layout).Positions;
        var cells = new CaptureSetDocumentCell[request.Images.Count];
        for (var index = 0; index < request.Images.Count; index++)
        {
            var position = positions[index];
            cells[index] = new CaptureSetDocumentCell(
                position.Row,
                position.Column,
                filePaths[index],
                request.Images[index].PixelWidth,
                request.Images[index].PixelHeight,
                index < request.Regions.Count
                    ? request.Regions[index]
                    : new CaptureRegion(0, 0, 0, 0));
        }

        var document = new CaptureSetDocument(
            DocumentVersion,
            request.Mode.ToString(),
            rows,
            columns,
            request.Matrix?.GapX ?? 0,
            request.Matrix?.GapY ?? 0,
            request.Padding,
            createdAt,
            cells);
        return JsonSerializer.Serialize(document, JsonOptions);
    }
}
