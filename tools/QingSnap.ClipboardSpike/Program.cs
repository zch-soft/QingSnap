using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Services;
using DrawingRectangle = System.Drawing.Rectangle;
using SelectionMode = QingSnap.App.Models.SelectionMode;

// Spike harness for the multi-image clipboard. It uses the production clipboard
// writer so Excel / Word / WPS paste behaviour can be inspected.
//
//   dotnet run --project tools/QingSnap.ClipboardSpike -- diag
//   dotnet run --project tools/QingSnap.ClipboardSpike -- 2 3 skip-excluded
//   dotnet run --project tools/QingSnap.ClipboardSpike -- html <output-directory>
if (args.Length > 0 && args[0] == "diag")
{
    Diag.Run();
    return;
}

if (args.Length > 1 && args[0] == "html")
{
    WriteHtmlSample(args[1]);
    return;
}

var rows = args.Length > 0 ? int.Parse(args[0]) : 2;
var columns = args.Length > 1 ? int.Parse(args[1]) : 3;
var skipExcluded = args.Length > 2 && args[2] == "skip-excluded";
const int cellWidth = 240;
const int cellHeight = 140;

var images = new List<BitmapSource>();
var regions = new List<CaptureRegion>();
var cells = new List<MatrixCell>();
for (var row = 1; row <= rows; row++)
{
    for (var column = 1; column <= columns; column++)
    {
        if (skipExcluded && row == 2 && column == 2)
        {
            continue;
        }

        images.Add(CreateCellImage(row, column, cellWidth, cellHeight));
        regions.Add(new CaptureRegion(
            (column - 1) * cellWidth,
            (row - 1) * cellHeight,
            cellWidth,
            cellHeight));
        cells.Add(new MatrixCell(row, column));
    }
}

using var clipboard = new ClipboardService();
var result = await clipboard.CopyCaptureSetAsync(new CaptureSetClipboardRequest(
    images,
    regions,
    SelectionMode.Matrix,
    new MatrixSpec(new CaptureRegion(0, 0, columns * cellWidth, rows * cellHeight), rows, columns, 8, 8),
    cells,
    regions[0],
    "Auto",
    "Spike",
    0,
    IncludeFileDrop: false));

Console.WriteLine(
    $"rows={result.Rows} columns={result.Columns} files={result.FilePaths.Count} html={result.HtmlByteLength}");
Console.WriteLine($"temp={result.TempDirectory}");
foreach (var path in result.FilePaths)
{
    Console.WriteLine(path);
}

Console.WriteLine("现在可在 Excel / Word / WPS 中按 Ctrl+V，检查图片是否按行列逐格粘贴。");
await Task.Delay(300);

static void WriteHtmlSample(string directory)
{
    Directory.CreateDirectory(directory);
    var rows = 2;
    var columns = 2;
    var table = new System.Text.StringBuilder("<table style=\"border-collapse:collapse\">");
    for (var row = 1; row <= rows; row++)
    {
        table.Append("<tr>");
        for (var column = 1; column <= columns; column++)
        {
            var path = Path.Combine(directory, $"Spike_{row}_{column}.png");
            using (var stream = File.Create(path))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(CreateCellImage(row, column, 120, 80)));
                encoder.Save(stream);
            }

            table.Append("<td style=\"padding:4px\"><img src=\"")
                .Append(new Uri(path).AbsoluteUri)
                .Append("\" width=\"120\" height=\"80\"></td>");
        }

        table.Append("</tr>");
    }

    table.Append("</table>");
    var path2 = Path.Combine(directory, "spike.html");
    File.WriteAllText(path2, "<html><body>" + table + "</body></html>");
    Console.WriteLine(path2);
}

static BitmapSource CreateCellImage(int row, int column, int width, int height)
{
    var stride = width * 4;
    var pixels = new byte[stride * height];
    var red = (byte)Math.Clamp(40 + row * 70, 0, 255);
    var green = (byte)Math.Clamp(40 + column * 60, 0, 255);
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var offset = y * stride + x * 4;
            var marker = (x < 12 && y < 60) || (y < 12 && x < 60);
            pixels[offset] = marker ? (byte)255 : (byte)30;
            pixels[offset + 1] = marker ? (byte)255 : green;
            pixels[offset + 2] = marker ? (byte)255 : red;
            pixels[offset + 3] = 255;
        }
    }

    var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    image.Freeze();
    return image;
}

internal static class Diag
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    private static extern IntPtr GetOpenClipboardWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    public static void Run()
    {
        var window = GetOpenClipboardWindow();
        Console.WriteLine($"open-window=0x{window.ToInt64():X}");
        if (window != IntPtr.Zero)
        {
            GetWindowThreadProcessId(window, out var processId);
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)processId);
                Console.WriteLine($"owner-pid={processId} owner={process.ProcessName}");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"owner-pid={processId} ({exception.Message})");
            }
        }

        Console.WriteLine($"open-clipboard={OpenClipboard(IntPtr.Zero)} error={Marshal.GetLastWin32Error()}");
        CloseClipboard();
    }
}
