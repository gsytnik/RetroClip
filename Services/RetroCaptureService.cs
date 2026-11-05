using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RetroClip.Services;

public sealed class RetroCaptureService : IAsyncDisposable
{
    public const int MaxBufferSeconds = 120;
    public const int MinBufferSeconds = 10;

    private readonly object _sync = new();
    private WasapiRecorder? _recorder;
    private CircularAudioBuffer? _ring;
    private WaveFormat? _format;
    private int _bufferSeconds = MaxBufferSeconds;
    private bool _isCapturing;

    public bool IsCapturing
    {
        get
        {
            lock (_sync)
            {
                return _isCapturing;
            }
        }
    }

    public int BufferSeconds
    {
        get
        {
            lock (_sync)
            {
                return _bufferSeconds;
            }
        }
        set
        {
            if (value is < MinBufferSeconds or > MaxBufferSeconds)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            lock (_sync)
            {
                if (_isCapturing)
                {
                    throw new InvalidOperationException("Stop capture before changing buffer length.");
                }

                _bufferSeconds = value;
            }
        }
    }

    public WaveFormat? WaveFormat
    {
        get
        {
            lock (_sync)
            {
                return _format;
            }
        }
    }

    public TimeSpan BufferedDuration
    {
        get
        {
            lock (_sync)
            {
                if (_ring is null || _format is null)
                {
                    return TimeSpan.Zero;
                }

                return _ring.GetDuration(_format.AverageBytesPerSecond);
            }
        }
    }

    public event EventHandler? BufferProgressChanged;
    public event EventHandler<Exception>? CaptureFaulted;

    public async Task StartAsync(uint processId, CancellationToken cancellationToken = default)
    {
        await StopAsync().ConfigureAwait(false);

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            throw new NotSupportedException("Process loopback requires Windows 10 version 2004 (build 19041) or later.");
        }

        var recorder = await new WasapiRecorderBuilder()
            .WithProcessLoopback(processId, ProcessLoopbackMode.IncludeTargetProcessTree)
            .BuildAsync()
            .ConfigureAwait(false);

        var format = recorder.WaveFormat;
        var capacity = checked(format.AverageBytesPerSecond * BufferSeconds);
        var ring = new CircularAudioBuffer(capacity);

        recorder.DataAvailable += OnDataAvailable;
        recorder.RecordingStopped += OnRecordingStopped;

        lock (_sync)
        {
            _recorder = recorder;
            _ring = ring;
            _format = format;
            _isCapturing = true;
        }

        recorder.StartRecording();
        BufferProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task StopAsync()
    {
        WasapiRecorder? recorder;
        lock (_sync)
        {
            recorder = _recorder;
            _recorder = null;
            _isCapturing = false;
        }

        if (recorder is null)
        {
            return Task.CompletedTask;
        }

        recorder.DataAvailable -= OnDataAvailable;
        recorder.RecordingStopped -= OnRecordingStopped;

        try
        {
            if (recorder.CaptureState != CaptureState.Stopped)
            {
                recorder.StopRecording();
            }
        }
        catch
        {
            // Best-effort stop; dispose still runs below.
        }

        recorder.Dispose();
        BufferProgressChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public string SaveClip(string directory, string? processName = null)
    {
        byte[] snapshot;
        WaveFormat format;

        lock (_sync)
        {
            if (_ring is null || _format is null)
            {
                throw new InvalidOperationException("Nothing to save yet. Start capture first.");
            }

            snapshot = _ring.Snapshot();
            format = _format;
        }

        if (snapshot.Length == 0)
        {
            throw new InvalidOperationException("The buffer is empty. Wait for the app to play audio.");
        }

        Directory.CreateDirectory(directory);
        var safeName = string.IsNullOrWhiteSpace(processName) ? "clip" : SanitizeFileName(processName);
        var path = Path.Combine(directory, $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

        using var writer = new WaveFileWriter(path, format);
        writer.Write(snapshot);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        _ = flags;
        _ = devicePosition;
        _ = qpcPosition;

        CircularAudioBuffer? ring;
        lock (_sync)
        {
            ring = _ring;
        }

        ring?.Write(buffer);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_sync)
        {
            _isCapturing = false;
        }

        if (e.Exception is not null)
        {
            CaptureFaulted?.Invoke(this, e.Exception);
        }

        BufferProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "clip" : name.Trim();
    }
}
