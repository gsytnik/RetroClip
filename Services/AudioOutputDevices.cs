using NAudio.CoreAudioApi;

namespace RetroClip.Services;

public static class AudioOutputDevices
{
    public static IReadOnlyList<AudioOutputDeviceInfo> ListActiveRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioOutputDeviceInfo(d.ID, d.FriendlyName))
            .OrderBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? GetDefaultRenderDeviceId()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device)
            || device is null)
        {
            return null;
        }

        using (device)
        {
            return device.ID;
        }
    }

    public static bool LooksLikeVirtualMicFeed(string friendlyName)
    {
        string[] good =
        [
            "cable input",
            "vb-audio",
            "voicemeeter input",
            "voicemeeter in ",
            "voicemeeter vaio",
            "voicemeeter aux input",
        ];

        return good.Any(h => friendlyName.Contains(h, StringComparison.OrdinalIgnoreCase));
    }
}
