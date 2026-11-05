using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RetroClip.Services;

public sealed class ClipPlayer : IDisposable
{
    private WasapiPlayer? _player;
    private WasapiPlayer? _secondaryPlayer;
    private AudioFileReader? _reader;
    private AudioFileReader? _secondaryReader;
    private ISampleProvider? _trimmed;
    private ISampleProvider? _secondaryTrimmed;
    private MMDevice? _outputDevice;
    private MMDevice? _secondaryDevice;
    private int _activePlayers;
    private bool _disposed;

    public PlaybackState State
    {
        get
        {
            if (_player?.PlaybackState == PlaybackState.Playing
                || _secondaryPlayer?.PlaybackState == PlaybackState.Playing)
            {
                return PlaybackState.Playing;
            }

            return PlaybackState.Stopped;
        }
    }

    public TimeSpan CurrentTime => _reader?.CurrentTime ?? TimeSpan.Zero;

    public event EventHandler? PlaybackStopped;

    public void PlayRange(
        string path,
        TimeSpan start,
        TimeSpan end,
        string? outputDeviceId = null,
        string? secondaryOutputDeviceId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (end <= start)
        {
            throw new ArgumentException("End must be after start.");
        }

        StopInternal(disposeReader: true);

        using var probe = new AudioFileReader(path);
        var duration = probe.TotalTime;
        start = Clamp(start, TimeSpan.Zero, duration);
        end = Clamp(end, start, duration);
        var take = end - start;

        _reader = CreateTrimmedReader(path, start, take, out _trimmed);
        _player = BuildPlayer(outputDeviceId, out _outputDevice);
        _player.PlaybackStopped += OnPrimaryStopped;
        _player.Init(_trimmed);
        _activePlayers = 1;

        if (!string.IsNullOrWhiteSpace(secondaryOutputDeviceId)
            && !string.Equals(secondaryOutputDeviceId, outputDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            _secondaryReader = CreateTrimmedReader(path, start, take, out _secondaryTrimmed);
            _secondaryPlayer = BuildPlayer(secondaryOutputDeviceId, out _secondaryDevice);
            _secondaryPlayer.PlaybackStopped += OnSecondaryStopped;
            _secondaryPlayer.Init(_secondaryTrimmed);
            _activePlayers = 2;
            _secondaryPlayer.Play();
        }

        _player.Play();
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopInternal(disposeReader: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopInternal(disposeReader: true);
        _disposed = true;
    }

    private static AudioFileReader CreateTrimmedReader(string path, TimeSpan start, TimeSpan take, out ISampleProvider trimmed)
    {
        var reader = new AudioFileReader(path)
        {
            CurrentTime = start
        };
        trimmed = reader.Take(take);
        return reader;
    }

    private static WasapiPlayer BuildPlayer(string? outputDeviceId, out MMDevice? device)
    {
        var builder = new WasapiPlayerBuilder();
        device = null;
        if (!string.IsNullOrWhiteSpace(outputDeviceId))
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(outputDeviceId);
            builder = builder.WithDevice(device);
        }

        return builder.Build();
    }

    private void OnPrimaryStopped(object? sender, StoppedEventArgs e) => HandlePlayerStopped();

    private void OnSecondaryStopped(object? sender, StoppedEventArgs e) => HandlePlayerStopped();

    private void HandlePlayerStopped()
    {
        var remaining = Interlocked.Decrement(ref _activePlayers);
        if (remaining <= 0)
        {
            PlaybackStopped?.Invoke(this, EventArgs.Empty);
        }
    }

    private void StopInternal(bool disposeReader)
    {
        Interlocked.Exchange(ref _activePlayers, 0);

        StopOne(ref _player, OnPrimaryStopped);
        StopOne(ref _secondaryPlayer, OnSecondaryStopped);

        _trimmed = null;
        _secondaryTrimmed = null;

        if (disposeReader)
        {
            _reader?.Dispose();
            _reader = null;
            _secondaryReader?.Dispose();
            _secondaryReader = null;
        }

        _outputDevice?.Dispose();
        _outputDevice = null;
        _secondaryDevice?.Dispose();
        _secondaryDevice = null;
    }

    private static void StopOne(ref WasapiPlayer? player, EventHandler<StoppedEventArgs> handler)
    {
        if (player is null)
        {
            return;
        }

        player.PlaybackStopped -= handler;
        try
        {
            if (player.PlaybackState != PlaybackState.Stopped)
            {
                player.Stop();
            }
        }
        catch
        {
            // Best-effort stop.
        }

        player.Dispose();
        player = null;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }
}
