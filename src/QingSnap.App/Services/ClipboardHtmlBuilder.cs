using System.IO;
using System.Net;
using System.Text;

namespace QingSnap.App.Services;

internal sealed record ClipboardTableCell(string FilePath, int Width, int Height);

internal sealed record ClipboardTable(
    int Rows,
    int Columns,
    IReadOnlyDictionary<(int Row, int Column), ClipboardTableCell> Cells,
    int CellPadding);

internal static class ClipboardHtmlBuilder
{
    private const string HeaderTemplate =
        "Version:0.9\r\n" +
        "StartHTML:{0:0000000000}\r\n" +
        "EndHTML:{1:0000000000}\r\n" +
        "StartFragment:{2:0000000000}\r\n" +
        "EndFragment:{3:0000000000}\r\n";

    private const string HtmlPrefix =
        "<html><head><meta charset=\"utf-8\"></head><body>\r\n<!--StartFragment-->";
    private const string HtmlSuffix = "<!--EndFragment-->\r\n</body></html>";

    public static string BuildFragment(ClipboardTable table)
    {
        var padding = Math.Clamp(table.CellPadding, 0, 24);
        var builder = new StringBuilder();
        builder.Append("<table style=\"border-collapse:collapse;border-spacing:0\">");
        for (var row = 1; row <= table.Rows; row++)
        {
            builder.Append("<tr>");
            for (var column = 1; column <= table.Columns; column++)
            {
                builder.Append("<td style=\"padding:").Append(padding).Append("px\">");
                if (table.Cells.TryGetValue((row, column), out var cell))
                {
                    builder.Append("<img src=\"")
                        .Append(WebUtility.HtmlEncode(ToFileUrl(cell.FilePath)))
                        .Append("\" width=\"")
                        .Append(Math.Max(1, cell.Width))
                        .Append("\" height=\"")
                        .Append(Math.Max(1, cell.Height))
                        .Append("\">");
                }

                builder.Append("</td>");
            }

            builder.Append("</tr>");
        }

        builder.Append("</table>");
        return builder.ToString();
    }

    public static string ToFileUrl(string filePath) =>
        new Uri(Path.GetFullPath(filePath)).AbsoluteUri;

    public static byte[] BuildCfHtml(string fragment)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var html = HtmlPrefix + fragment + HtmlSuffix;
        var headerLength = encoding.GetByteCount(string.Format(HeaderTemplate, 0, 0, 0, 0));
        var startHtml = headerLength;
        var startFragment = startHtml + encoding.GetByteCount(HtmlPrefix);
        var endFragment = startFragment + encoding.GetByteCount(fragment);
        var endHtml = startHtml + encoding.GetByteCount(html);
        var header = string.Format(
            HeaderTemplate,
            startHtml,
            endHtml,
            startFragment,
            endFragment);
        return encoding.GetBytes(header + html);
    }

    public static int ToCssPixels(double pixelSize, double dpi)
    {
        var scale = dpi > 0 ? 96.0 / dpi : 1.0;
        return Math.Max(1, (int)Math.Round(pixelSize * scale, MidpointRounding.AwayFromZero));
    }
}
