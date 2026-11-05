using System.IO;
using NAudio.Wave;

namespace RetroClip.Services;

public static class ClipTrimmer
{
    public static string SaveTrimmed(
        string sourcePath,
        TimeSpan start,
        TimeSpan end,
        string? outputPath = null,
        bool overwriteSource = false,
        bool deleteOriginal = false)
    {
        if (end <= start)
        {
            throw new ArgumentException("End must be after start.");
        }

        sourcePath = Path.GetFullPath(sourcePath);

        string destination;
        if (overwriteSource)
        {
            destination = Path.Combine(Path.GetTempPath(), $"retroclip_trim_{Guid.NewGuid():N}.wav");
        }
        else if (!string.IsNullOrWhiteSpace(outputPath))
        {
            destination = Path.GetFullPath(outputPath);
        }
        else
        {
            var dir = Path.GetDirectoryName(sourcePath) ?? ".";
            var name = Path.GetFileNameWithoutExtension(sourcePath);
            destination = Path.GetFullPath(Path.Combine(dir, $"{name}_trim_{DateTime.Now:yyyyMMdd_HHmmss}.wav"));
        }

        var sameAsSource = string.Equals(destination, sourcePath, StringComparison.OrdinalIgnoreCase);
        if (sameAsSource)
        {
            overwriteSource = true;
            destination = Path.Combine(Path.GetTempPath(), $"retroclip_trim_{Guid.NewGuid():N}.wav");
        }

        // Read/write must finish and release the source handle before any delete/replace.
        WriteTrimmedFile(sourcePath, start, end, destination);

        if (overwriteSource)
        {
            File.Copy(destination, sourcePath, overwrite: true);
            TryDelete(destination);
            return sourcePath;
        }

        if (deleteOriginal)
        {
            if (!File.Exists(sourcePath))
            {
                throw new IOException($"Trimmed file was saved, but the original was already missing: {sourcePath}");
            }

            TryDelete(sourcePath);
            if (File.Exists(sourcePath))
            {
                throw new IOException($"Trimmed file was saved to {destination}, but the original could not be deleted: {sourcePath}");
            }
        }

        return destination;
    }

    private static void WriteTrimmedFile(string sourcePath, TimeSpan start, TimeSpan end, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        using (var source = new AudioFileReader(sourcePath))
        {
            var duration = source.TotalTime;
            start = TimeSpan.FromSeconds(Math.Clamp(start.TotalSeconds, 0, duration.TotalSeconds));
            end = TimeSpan.FromSeconds(Math.Clamp(end.TotalSeconds, start.TotalSeconds, duration.TotalSeconds));
            var take = end - start;

            source.CurrentTime = start;
            var trimmed = source.Take(take);
            WaveFileWriter.CreateWaveFile16(destination, trimmed);
        }
    }

    private static void TryDelete(string path)
    {
        const int attempts = 8;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                if (!File.Exists(path))
                {
                    return;
                }
            }
            catch (IOException) when (i < attempts - 1)
            {
                Thread.Sleep(40 * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < attempts - 1)
            {
                Thread.Sleep(40 * (i + 1));
            }
        }

        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    public static string BuildOutputPath(string directory, string fileNameWithoutExtension)
    {
        var safe = SanitizeFileName(fileNameWithoutExtension);
        return Path.Combine(directory, safe + ".wav");
    }

    public static string SanitizeFileName(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? $"clip_{DateTime.Now:yyyyMMdd_HHmmss}" : name.Trim();
    }
}
