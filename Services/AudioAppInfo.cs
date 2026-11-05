namespace RetroClip.Services;

public sealed record AudioAppInfo(uint ProcessId, string DisplayName, string ProcessName)
{
    public override string ToString() => $"{DisplayName}  (PID {ProcessId})";
}
