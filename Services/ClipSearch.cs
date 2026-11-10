namespace RetroClip.Services;

public static class ClipSearch
{
    public static IReadOnlyList<ClipInfo> Filter(IReadOnlyList<ClipInfo> clips, string? query)
    {
        if (clips.Count == 0)
        {
            return clips;
        }

        var trimmed = query?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return clips;
        }

        return clips
            .Where(c => c.FileName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
