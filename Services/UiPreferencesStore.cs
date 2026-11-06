using System.IO;
using System.Text.Json;

namespace RetroClip.Services;

public sealed class UiPreferences
{
    public string? CaptureProcessName { get; set; }
    public int? BufferSeconds { get; set; }
    public string? EditPreviewDeviceId { get; set; }
    public string? EditClipPath { get; set; }
    public string? PlaybackPreviewDeviceId { get; set; }
    public string? PlaybackStreamDeviceId { get; set; }
    public string? PlaybackClipPath { get; set; }
    public bool AlsoHearLocally { get; set; } = true;
    public string? SoundboardClipPath { get; set; }
}

public sealed class UiPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;

    public UiPreferences Current { get; }

    public UiPreferencesStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RetroClip");
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "preferences.json");
        Current = Load() ?? new UiPreferences();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_filePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch
        {
            // Preferences are best-effort; ignore write failures.
        }
    }

    private UiPreferences? Load()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<UiPreferences>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
