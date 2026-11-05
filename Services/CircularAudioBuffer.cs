namespace RetroClip.Services;

/// <summary>
/// Thread-safe ring buffer that retains the most recent PCM bytes up to a fixed capacity.
/// </summary>
public sealed class CircularAudioBuffer
{
    private readonly byte[] _buffer;
    private readonly object _sync = new();
    private int _writePos;
    private long _bytesWritten;
    private int _filled;

    public CircularAudioBuffer(int capacityBytes)
    {
        if (capacityBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        }

        _buffer = new byte[capacityBytes];
    }

    public int Capacity => _buffer.Length;

    public int FilledBytes
    {
        get
        {
            lock (_sync)
            {
                return _filled;
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _writePos = 0;
            _bytesWritten = 0;
            _filled = 0;
            Array.Clear(_buffer);
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            var remaining = data;
            while (!remaining.IsEmpty)
            {
                var chunk = Math.Min(remaining.Length, _buffer.Length - _writePos);
                remaining[..chunk].CopyTo(_buffer.AsSpan(_writePos, chunk));
                _writePos = (_writePos + chunk) % _buffer.Length;
                remaining = remaining[chunk..];
            }

            _bytesWritten += data.Length;
            _filled = (int)Math.Min(_buffer.Length, _bytesWritten);
        }
    }

    /// <summary>
    /// Returns a contiguous chronological copy of the currently buffered audio.
    /// </summary>
    public byte[] Snapshot()
    {
        lock (_sync)
        {
            if (_filled == 0)
            {
                return Array.Empty<byte>();
            }

            var result = new byte[_filled];
            if (_filled < _buffer.Length)
            {
                Buffer.BlockCopy(_buffer, 0, result, 0, _filled);
                return result;
            }

            var tail = _buffer.Length - _writePos;
            Buffer.BlockCopy(_buffer, _writePos, result, 0, tail);
            Buffer.BlockCopy(_buffer, 0, result, tail, _writePos);
            return result;
        }
    }

    public TimeSpan GetDuration(int averageBytesPerSecond)
    {
        if (averageBytesPerSecond <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(FilledBytes / (double)averageBytesPerSecond);
    }
}
