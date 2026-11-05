using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace RetroClip.Services;

public static class AudioAppEnumerator
{
    public static IReadOnlyList<AudioAppInfo> ListActiveRenderApps()
    {
        var results = new Dictionary<uint, AudioAppInfo>();

        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    var processId = session.GetProcessID;
                    if (processId == 0 || results.ContainsKey(processId))
                    {
                        continue;
                    }

                    string processName;
                    string displayName;
                    try
                    {
                        using var process = Process.GetProcessById((int)processId);
                        processName = process.ProcessName;
                        displayName = string.IsNullOrWhiteSpace(process.MainWindowTitle)
                            ? process.ProcessName
                            : $"{process.ProcessName} — {process.MainWindowTitle}";
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }
                    catch (InvalidOperationException)
                    {
                        continue;
                    }

                    var sessionDisplay = session.DisplayName;
                    if (!string.IsNullOrWhiteSpace(sessionDisplay) &&
                        !sessionDisplay.Equals(processName, StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = $"{processName} — {sessionDisplay}";
                    }

                    results[processId] = new AudioAppInfo(processId, displayName, processName);
                }
            }
        }

        return results.Values
            .OrderBy(a => a.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.ProcessId)
            .ToList();
    }
}
