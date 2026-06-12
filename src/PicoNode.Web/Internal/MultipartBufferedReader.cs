using System.Buffers;

namespace PicoNode.Web.Internal;

/// <summary>
/// Stateful buffered reader for multipart/form-data streams.
/// Provides line-by-line and boundary-delimited reading with
/// cross-chunk boundary detection.
/// </summary>
internal sealed class MultipartBufferedReader : IDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private int _buffered;
    private int _position;
    private bool _endOfStream;

    public MultipartBufferedReader(Stream stream, int bufferSize = 65536)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
    }

    /// <summary>Reads a line until \r\n. Returns null at end of stream.</summary>
    public async ValueTask<byte[]?> ReadLineAsync()
    {
        while (true)
        {
            // Search for \n in current buffer
            for (var i = _position; i < _buffered; i++)
            {
                if (_buffer[i] == (byte)'\n')
                {
                    // Check for \r before \n
                    var lineEnd = i > _position && _buffer[i - 1] == (byte)'\r' ? i - 1 : i;
                    var line = new byte[lineEnd - _position];
                    if (line.Length > 0)
                        Array.Copy(_buffer, _position, line, 0, line.Length);
                    _position = i + 1;
                    return line;
                }
            }

            if (_endOfStream)
                return null;

            await FillBufferAsync();
        }
    }

    /// <summary>Reads content until the boundary delimiter (\r\n--boundary or --boundary--). 
    /// Returns the content bytes, or null if the stream ends unexpectedly.</summary>
    public async ValueTask<byte[]?> ReadUntilBoundaryAsync(byte[] boundary)
    {
        await EnsureBufferedAsync();
        using var accumulator = new MemoryStream();

        while (true)
        {
            var matchIdx = FindBoundaryInBuffer(boundary);
            if (matchIdx >= 0)
            {
                // Flush content before boundary to accumulator
                var contentLen = matchIdx - _position;
                if (contentLen > 0)
                {
                    // If content starts with \r\n, those belong to the boundary, not content
                    var skipCrlf = _buffer[_position] == (byte)'\r' ? 2 : 0;
                    if (contentLen > skipCrlf)
                        accumulator.Write(_buffer, _position + skipCrlf, contentLen - skipCrlf);
                }

                _position = matchIdx + 2 + 2 + boundary.Length;
                // Skip trailing -- (terminating boundary) or \r\n
                var rem = _buffered - _position;
                if (rem >= 2 && _buffer[_position] == (byte)'-')
                    _position += 2;
                if (rem >= 2 && _buffer[_position] == (byte)'\r')
                    _position += 2;

                return accumulator.ToArray();
            }

            // No boundary found. Copy all data except the last (boundary.Length + 4)
            // bytes (max partial boundary prefix) into the accumulator.
            var safe = _position;
            var keep = Math.Min(_buffered - safe, boundary.Length + 4);
            var flush = (_buffered - safe) - keep;
            if (flush > 0)
                accumulator.Write(_buffer, safe, flush);

            // Keep only the trailing bytes that might be a partial boundary match
            var srcPos = safe + flush;
            if (keep > 0 && srcPos + keep <= _buffered + flush)
                Array.Copy(_buffer, srcPos, _buffer, 0, keep);
            _buffered = keep;
            _position = 0;

            if (_endOfStream)
                return accumulator.Length > 0 ? accumulator.ToArray() : null;

            await FillBufferAsync();
        }
    }

    private int FindBoundaryInBuffer(byte[] boundary)
    {
        // Search from _position to _buffered - boundary.Length
        var searchEnd = _buffered - boundary.Length;
        for (var i = _position; i <= searchEnd; i++)
        {
            // Look for \r\n-- or -- at start
            if (i > _position && i + 1 < _buffered
                && _buffer[i] == (byte)'\r' && _buffer[i + 1] == (byte)'\n'
                && i + 3 < _buffered && _buffer[i + 2] == (byte)'-' && _buffer[i + 3] == (byte)'-')
            {
                // Check if this is our boundary
                var bStart = i + 4;
                if (MatchesBoundary(bStart, boundary))
                    return i;
                i += 3; // skip past \r\n--
            }
            // Check for boundary at position 0 (start of multipart body)
            else if (i == _position && _buffer[i] == (byte)'-' && _buffer[i + 1] == (byte)'-')
            {
                if (MatchesBoundary(i + 2, boundary))
                    return i;
            }
        }
        return -1;
    }

    private bool MatchesBoundary(int start, byte[] boundary)
    {
        if (start + boundary.Length > _buffered)
            return false;
        for (var j = 0; j < boundary.Length; j++)
        {
            if (_buffer[start + j] != boundary[j])
                return false;
        }
        return true;
    }

    private async ValueTask EnsureBufferedAsync()
    {
        if (_buffered - _position < 256 && !_endOfStream)
            await FillBufferAsync();
    }

    private async ValueTask FillBufferAsync()
    {
        if (_endOfStream) return;

        // Compact: move unread data to the front to free space at the end.
        if (_position > 0 && _buffered > _position)
        {
            var unread = _buffered - _position;
            Array.Copy(_buffer, _position, _buffer, 0, unread);
            _buffered = unread;
            _position = 0;
        }

        var freeStart = _buffered;
        var freeSize = _buffer.Length - freeStart;
        if (freeSize <= 0)
        {
            // Buffer completely full. Compact by keeping trailing data
            // at the front, capped at buffer capacity.
            var keepSize = Math.Min(_buffered, _buffer.Length / 2);
            var keepStart = _buffered - keepSize;
            Array.Copy(_buffer, keepStart, _buffer, 0, keepSize);
            _buffered = keepSize;
            _position = 0;
            freeStart = _buffered;
            freeSize = _buffer.Length - freeStart;
        }

        try
        {
            var read = await _stream.ReadAsync(_buffer.AsMemory(freeStart, freeSize));
            if (read == 0)
            {
                _endOfStream = true;
                return;
            }
            _buffered += read;
        }
        catch
        {
            _endOfStream = true;
        }
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
    }
}
