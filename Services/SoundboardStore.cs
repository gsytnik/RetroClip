using System.IO;
using System.Text.Json;

namespace RetroClip.Services;

public sealed class SoundboardStore
{
    public const int SlotCount = 16;
    public const string DefaultPresetName = "Default";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly Dictionary<string, string?[]> _presets = new(StringComparer.OrdinalIgnoreCase);
    private readonly string?[] _slots = new string?[SlotCount];
    private string _activePresetName = DefaultPresetName;

    public SoundboardStore(string clipsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipsDirectory);
        Directory.CreateDirectory(clipsDirectory);
        _filePath = Path.Combine(clipsDirectory, "soundboard.json");
        Load();
    }

    public string ActivePresetName => _activePresetName;

    public IReadOnlyList<string> ListPresetNames()
    {
        return _presets.Keys
            .OrderBy(n => string.Equals(n, DefaultPresetName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string? GetPath(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SlotCount);
        return _slots[index];
    }

    public void SetPath(int index, string? path)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SlotCount);

        // Working board only — persist happens on Save (typed name, else selected).
        _slots[index] = string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public bool LoadPreset(string name)
    {
        var key = NormalizeName(name);
        if (key is null || !_presets.TryGetValue(key, out var preset))
        {
            return false;
        }

        CopySlots(preset, _slots);
        _activePresetName = key;
        SaveFile();
        return true;
    }

    public bool SavePreset(string name)
    {
        var key = NormalizeName(name);
        if (key is null)
        {
            return false;
        }

        // Write working board to exactly one preset name.
        _presets[key] = CloneSlots(_slots);
        _activePresetName = key;
        SaveFile();
        return true;
    }

    public bool DeletePreset(string name)
    {
        var key = NormalizeName(name);
        if (key is null
            || string.Equals(key, DefaultPresetName, StringComparison.OrdinalIgnoreCase)
            || !_presets.ContainsKey(key))
        {
            return false;
        }

        _presets.Remove(key);
        if (string.Equals(_activePresetName, key, StringComparison.OrdinalIgnoreCase))
        {
            LoadPreset(DefaultPresetName);
        }
        else
        {
            SaveFile();
        }

        return true;
    }

    private void Load()
    {
        _presets.Clear();
        Array.Clear(_slots);

        if (!File.Exists(_filePath))
        {
            EnsureDefaultPreset();
            CopySlots(_presets[DefaultPresetName], _slots);
            _activePresetName = DefaultPresetName;
            SaveFile();
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("Presets", out var presetsElement)
                && presetsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in presetsElement.EnumerateObject())
                {
                    var name = NormalizeName(property.Name);
                    if (name is null)
                    {
                        continue;
                    }

                    _presets[name] = ReadSlots(property.Value);
                }

                if (root.TryGetProperty("ActivePreset", out var activeElement)
                    && activeElement.ValueKind == JsonValueKind.String)
                {
                    var active = NormalizeName(activeElement.GetString());
                    if (active is not null)
                    {
                        _activePresetName = active;
                    }
                }
            }
            else if (root.TryGetProperty("Slots", out var slotsElement))
            {
                // Migrate legacy single-board format.
                _presets[DefaultPresetName] = ReadSlots(slotsElement);
                _activePresetName = DefaultPresetName;
            }
        }
        catch
        {
            // Keep empty board if the file is corrupt.
        }

        EnsureDefaultPreset();
        if (!_presets.ContainsKey(_activePresetName))
        {
            _activePresetName = DefaultPresetName;
        }

        CopySlots(_presets[_activePresetName], _slots);
        SaveFile();
    }

    private void EnsureDefaultPreset()
    {
        if (!_presets.ContainsKey(DefaultPresetName))
        {
            _presets[DefaultPresetName] = new string?[SlotCount];
        }
    }

    private void SaveFile()
    {
        EnsureDefaultPreset();
        var data = new SoundboardFileData
        {
            ActivePreset = _activePresetName,
            Presets = _presets.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Select(s => s ?? string.Empty).ToList(),
                StringComparer.OrdinalIgnoreCase)
        };
        File.WriteAllText(_filePath, JsonSerializer.Serialize(data, JsonOptions));
    }

    private static string?[] ReadSlots(JsonElement element)
    {
        var slots = new string?[SlotCount];
        if (element.ValueKind != JsonValueKind.Array)
        {
            return slots;
        }

        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (index >= SlotCount)
            {
                break;
            }

            var path = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            slots[index] = string.IsNullOrWhiteSpace(path) ? null : path;
            index++;
        }

        return slots;
    }

    private static string?[] CloneSlots(string?[] source)
    {
        var copy = new string?[SlotCount];
        Array.Copy(source, copy, SlotCount);
        return copy;
    }

    private static void CopySlots(string?[] source, string?[] destination)
    {
        Array.Clear(destination);
        var count = Math.Min(SlotCount, source.Length);
        Array.Copy(source, destination, count);
    }

    private static string? NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private sealed class SoundboardFileData
    {
        public string ActivePreset { get; set; } = DefaultPresetName;
        public Dictionary<string, List<string>> Presets { get; set; } = new();
    }
}
