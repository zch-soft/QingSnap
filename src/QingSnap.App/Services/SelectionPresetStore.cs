using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class SelectionPresetStore
{
    private const int CurrentVersion = 1;
    private const int MaxNameLength = 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _presetPath;
    private readonly object _sync = new();

    public SelectionPresetStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _presetPath = Path.Combine(dataDirectory, "selection-presets.json");
    }

    public event EventHandler? Changed;

    public IReadOnlyList<SelectionPreset> LoadAll()
    {
        lock (_sync)
        {
            return LoadCore()
                .OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
    }

    public SelectionPreset? Find(Guid id) =>
        LoadAll().FirstOrDefault(preset => preset.Id == id);

    public SelectionPreset Add(string name, SelectionSet selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var now = DateTimeOffset.UtcNow;
        var preset = new SelectionPreset(
            Guid.NewGuid(),
            NormalizeName(name),
            selection,
            now,
            now);
        lock (_sync)
        {
            var presets = LoadCore().ToList();
            presets.Add(preset);
            SaveCore(presets);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return preset;
    }

    public bool Rename(Guid id, string name)
    {
        lock (_sync)
        {
            var presets = LoadCore().ToList();
            var index = presets.FindIndex(preset => preset.Id == id);
            if (index < 0)
            {
                return false;
            }

            presets[index] = presets[index] with
            {
                Name = NormalizeName(name),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            SaveCore(presets);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool ReplaceSelection(Guid id, SelectionSet selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        lock (_sync)
        {
            var presets = LoadCore().ToList();
            var index = presets.FindIndex(preset => preset.Id == id);
            if (index < 0)
            {
                return false;
            }

            presets[index] = presets[index] with
            {
                Selection = selection,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            SaveCore(presets);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Delete(Guid id)
    {
        lock (_sync)
        {
            var presets = LoadCore().ToList();
            if (presets.RemoveAll(preset => preset.Id == id) == 0)
            {
                return false;
            }

            SaveCore(presets);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static string NormalizeName(string? name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "未命名选区" : name.Trim();
        return value.Length > MaxNameLength ? value[..MaxNameLength] : value;
    }

    private List<SelectionPreset> LoadCore()
    {
        try
        {
            if (!File.Exists(_presetPath))
            {
                return [];
            }

            var state = JsonSerializer.Deserialize<PersistedState>(
                File.ReadAllText(_presetPath),
                JsonOptions);
            return state?.Presets?
                .Where(preset => preset.Id != Guid.Empty &&
                                 !string.IsNullOrWhiteSpace(preset.Name) &&
                                 preset.Selection is { HasSelection: true })
                .ToList() ?? [];
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

    private void SaveCore(IReadOnlyList<SelectionPreset> presets)
    {
        var temporaryPath = _presetPath + ".tmp";
        var json = JsonSerializer.Serialize(
            new PersistedState(CurrentVersion, presets),
            JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _presetPath, true);
    }

    private sealed record PersistedState(
        int Version,
        IReadOnlyList<SelectionPreset> Presets);
}
