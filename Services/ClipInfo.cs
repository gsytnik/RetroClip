namespace RetroClip.Services;

public sealed record ClipInfo(string Path, string FileName, TimeSpan Duration, DateTime LastWriteTimeUtc)
{
    public override string ToString() => FileName;
}
