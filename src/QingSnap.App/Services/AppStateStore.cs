using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class AppStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _statePath;

    public AppStateStore() : this(GetDefaultStatePath())
    {
    }

    internal AppStateStore(string statePath)
    {
        _statePath = statePath;
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
    }

    private static string GetDefaultStatePath()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QingSnap");
        return Path.Combine(dataDirectory, "state.json");
    }

    public CaptureRegion? LoadLastRegion() => LoadRecentRegions().FirstOrDefault();

    public IReadOnlyList<CaptureRegion> LoadRecentRegions()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return [];
            }

            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_statePath), JsonOptions);
            var recent = state?.RecentRegions?
                .Where(region => region.IsValid)
                .Take(CaptureRegionHistory.Capacity)
                .ToArray();
            if (recent is { Length: > 0 })
            {
                return recent;
            }

            return state?.LastRegion is { IsValid: true } legacyRegion
                ? [legacyRegion]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    public void SaveLastRegion(CaptureRegion region)
    {
        var recentRegions = CaptureRegionHistory.Add(LoadRecentRegions(), region);
        WriteState(new PersistedState(
            region,
            recentRegions,
            SelectionSet.Single(region)));
    }

    public void SaveLastSelectionSet(SelectionSet selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var regions = selection.Regions?
            .Where(region => region.IsValid)
            .ToArray() ?? [];
        if (regions.Length == 0)
        {
            return;
        }

        var recentRegions = CaptureRegionHistory.Add(LoadRecentRegions(), regions[0]);
        WriteState(new PersistedState(
            regions[0],
            recentRegions,
            selection with { Regions = regions }));
    }

    public SelectionSet? LoadLastSelectionSet()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<PersistedState>(
                File.ReadAllText(_statePath),
                JsonOptions);
            if (state?.LastSelectionSet is { HasSelection: true } selection)
            {
                return selection;
            }

            return state?.LastRegion is { IsValid: true } legacyRegion
                ? SelectionSet.Single(legacyRegion)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void WriteState(PersistedState state)
    {
        var temporaryPath = _statePath + ".tmp";
        var json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _statePath, true);
    }

    private sealed record PersistedState(
        CaptureRegion? LastRegion,
        IReadOnlyList<CaptureRegion>? RecentRegions = null,
        SelectionSet? LastSelectionSet = null);
}

internal static class CaptureRegionHistory
{
    public const int Capacity = 5;

    public static IReadOnlyList<CaptureRegion> Add(
        IEnumerable<CaptureRegion> existing,
        CaptureRegion region) =>
        existing
            .Where(candidate => candidate.IsValid && candidate != region)
            .Prepend(region)
            .Take(Capacity)
            .ToArray();

    public static int NextIndex(int currentIndex, int count) =>
        count <= 0 ? -1 : (currentIndex + 1 + count) % count;
}
