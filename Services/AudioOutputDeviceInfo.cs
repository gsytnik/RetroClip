namespace RetroClip.Services;

public sealed record AudioOutputDeviceInfo(string Id, string FriendlyName)
{
    public override string ToString() => FriendlyName;
}
