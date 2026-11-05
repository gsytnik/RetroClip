using System.IO;
using NAudio.Wave;

namespace RetroClip.Services;

public static class ClipLibrary
{
    public static IReadOnlyList<ClipInfo> ListClips(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<ClipInfo>();
        }

        var clips = new List<ClipInfo>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.wav")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                using var reader = new AudioFileReader(path);
                clips.Add(new ClipInfo(
                    path,
                    Path.GetFileName(path),
                    reader.TotalTime,
                    File.GetLastWriteTimeUtc(path)));
            }
            catch
            {
                // Skip unreadable files.
            }
        }

        return clips;
    }
}
