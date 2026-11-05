using System.IO;
using System.Text.Json;

namespace RetroClip.Services;

public sealed class SoundboardStore
{
    public const int SlotCount = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly string?[] _slots = new string?[SlotCount];

    public SoundboardStore(string clipsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipsDirectory);
        Directory.CreateDirectory(clipsDirectory);
        _filePath = Path.Combine(clipsDirectory, "soundboard.json");
        Load();
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

        _slots[index] = string.IsNullOrWhiteSpace(path) ? null : path;
        Save();
    }

    private void Load()
    {
        Array.Clear(_slots);
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<SoundboardData>(json, JsonOptions);
            if (data?.Slots is null)
            {
                return;
            }

            for (var i = 0; i < SlotCount && i < data.Slots.Count; i++)
            {
                var path = data.Slots[i];
                _slots[i] = string.IsNullOrWhiteSpace(path) ? null : path;
            }
        }
        catch
        {
            // Keep empty pads if the file is corrupt.
        }
    }

    private void Save()
    {
        var data = new SoundboardData
        {
            Slots = _slots.Select(s => s ?? string.Empty).ToList()
        };
        File.WriteAllText(_filePath, JsonSerializer.Serialize(data, JsonOptions));
    }

    private sealed class SoundboardData
    {
        public List<string> Slots { get; set; } = new();
    }
}
