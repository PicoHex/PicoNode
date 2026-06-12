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

        while (true)
        {
            // Search for \r\n--boundary or --boundary-- in the buffer
            var matchIdx = FindBoundaryInBuffer(boundary);
            if (matchIdx >= 0)
            {
                var contentLen = matchIdx - _position;
                // Skip leading \r\n if present (content before boundary)
                if (contentLen > 0 && _buffer[_position] == (byte)'\r')
                    contentLen -= 2; // skip \r\n before boundary

                var content = contentLen > 0 ? new byte[contentLen] : [];
                if (contentLen > 0)
                    Array.Copy(_buffer, _position, content, 0, contentLen);

                // Advance position past the boundary
                _position = matchIdx + (contentLen > 0 ? 2 : 0) + 2 + boundary.Length; // \r\n or start + -- + boundary
                // Skip potential -- (terminating boundary) or \r\n
                var remaining = _buffered - _position;
                if (remaining >= 2 && _buffer[_position] == (byte)'-' && _buffer[_position + 1] == (byte)'-')
                    _position += 2; // terminating boundary --
                if (remaining >= 2 && _buffer[_position] == (byte)'\r' && _buffer[_position + 1] == (byte)'\n')
                    _position += 2;

                return content;
            }

            // Compact buffer: move unread data to start
            if (_position > 0)
            {
                var unread = _buffered - _position;
                if (unread > 0)
                    Array.Copy(_buffer, _position, _buffer, 0, unread);
                _buffered = unread;
                _position = 0;
            }

            if (_endOfStream)
                return null;

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

        var freeStart = _buffered;
        var freeSize = _buffer.Length - freeStart;
        if (freeSize <= 0) return;

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
