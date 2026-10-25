using System.IO;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

internal static class HistoryGroupFilter
{
    public static bool ShouldShow(HistoryItem item, bool fold, ISet<string> visibleGroups)
    {
        if (!fold || item.GroupId is not { Length: > 0 } groupId)
        {
            return true;
        }

        return visibleGroups.Add(groupId);
    }

    public static SelectionGroupManifest? ReadGroupForFile(
        string filePath,
        IDictionary<string, SelectionGroupManifest?> cache)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        if (!cache.TryGetValue(directory, out var manifest))
        {
            manifest = SelectionGroupStore.TryRead(directory);
            cache[directory] = manifest;
        }

        return manifest;
    }
}
