using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

internal static class SelectionGroupStore
{
    public const string FileName = ".qingsnap-group.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string GetManifestPath(string groupDirectory) =>
        Path.Combine(groupDirectory, FileName);

    public static void Write(string groupDirectory, SelectionGroupManifest manifest)
    {
        Directory.CreateDirectory(groupDirectory);
        var path = GetManifestPath(groupDirectory);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, JsonOptions));
        File.Move(temporaryPath, path, true);
    }

    public static SelectionGroupManifest? TryRead(string groupDirectory)
    {
        try
        {
            var path = GetManifestPath(groupDirectory);
            if (!File.Exists(path))
            {
                return null;
            }

            var manifest = JsonSerializer.Deserialize<SelectionGroupManifest>(
                File.ReadAllText(path),
                JsonOptions);
            return IsUsable(manifest, groupDirectory) ? manifest : null;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warning("HistoryGroup", $"读取多图组标记失败：{exception.Message}");
            return null;
        }
    }

    public static SelectionGroupManifest? TryReadForFile(string filePath) =>
        TryRead(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty);

    private static bool IsUsable(SelectionGroupManifest? manifest, string groupDirectory) =>
        manifest is not null &&
        manifest.Version is > 0 and <= SelectionGroupManifest.CurrentVersion &&
        manifest.GroupId.Length > 0 &&
        manifest.Count > 0 &&
        Directory.Exists(groupDirectory);
}
