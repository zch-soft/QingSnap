using System.Globalization;
using System.IO;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

internal static class SelectionOutputNaming
{
    public const string DefaultPrefix = "QingSnap";

    public static string NormalizePrefix(string? prefix)
    {
        var value = string.IsNullOrWhiteSpace(prefix) ? DefaultPrefix : prefix.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        var normalized = builder.ToString().Trim().TrimEnd('.');
        return normalized.Length == 0 ? DefaultPrefix : normalized;
    }

    public static string GetExtension(string? format) => format?.ToUpperInvariant() switch
    {
        "JPG" or "JPEG" => ".jpg",
        "BMP" => ".bmp",
        _ => ".png"
    };

    public static string FreeFileName(string prefix, int index, int count, string extension)
    {
        var digits = Math.Max(1, count.ToString(CultureInfo.InvariantCulture).Length);
        return $"{NormalizePrefix(prefix)}_{index.ToString($"D{digits}", CultureInfo.InvariantCulture)}{NormalizeExtension(extension)}";
    }

    public static string MatrixFileName(
        string prefix,
        int row,
        int column,
        int rows,
        int columns,
        string extension)
    {
        var rowDigits = Math.Max(1, rows.ToString(CultureInfo.InvariantCulture).Length);
        var columnDigits = Math.Max(1, columns.ToString(CultureInfo.InvariantCulture).Length);
        return $"{NormalizePrefix(prefix)}_{row.ToString($"D{rowDigits}", CultureInfo.InvariantCulture)}_{column.ToString($"D{columnDigits}", CultureInfo.InvariantCulture)}{NormalizeExtension(extension)}";
    }

    public static string MultiFileName(
        string prefix,
        int index,
        int count,
        MatrixSpec? matrix,
        IReadOnlyList<MatrixCell>? cells,
        string extension)
    {
        if (matrix is { } spec && cells is not null && index >= 0 && index < cells.Count)
        {
            var cell = cells[index];
            return MatrixFileName(prefix, cell.Row, cell.Column, spec.Rows, spec.Columns, extension);
        }

        return FreeFileName(prefix, index + 1, count, extension);
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".png";
        }

        return extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
    }
}
