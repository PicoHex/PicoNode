namespace PicoNode.Http.Benchmarks;

internal static class HttpResponseReader
{
    public static HttpResponseSnapshot Parse(byte[] responseBytes)
    {
        var headerLength = FindHeaderLength(responseBytes);
        if (headerLength < 0)
        {
            throw new InvalidOperationException("HTTP response header terminator was not found.");
        }

        var headerText = Encoding.ASCII.GetString(responseBytes, 0, headerLength);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 1; index < lines.Length; index++)
        {
            var colonIndex = lines[index].IndexOf(':');
            if (colonIndex <= 0)
            {
                throw new InvalidOperationException("Invalid HTTP response header.");
            }

            headers.Add(lines[index][..colonIndex], lines[index][(colonIndex + 1)..].Trim());
        }

        var contentLength = headers.TryGetValue("Content-Length", out var contentLengthValue)
            ? int.Parse(contentLengthValue, CultureInfo.InvariantCulture)
            : 0;
        var body = new byte[contentLength];
        var bufferedBodyCount = responseBytes.Length - (headerLength + 4);
        var copiedBodyCount = Math.Min(bufferedBodyCount, contentLength);

        if (copiedBodyCount > 0)
        {
            Array.Copy(responseBytes, headerLength + 4, body, 0, copiedBodyCount);
        }

        return new HttpResponseSnapshot(lines[0], headers, body);
    }

    public static HttpResponseSnapshot Read(NetworkStream stream)
    {
        using var buffer = new MemoryStream();

        while (true)
        {
            var chunk = new byte[256];
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
            {
                throw new InvalidOperationException(
                    "Connection closed while reading HTTP response."
                );
            }

            buffer.Write(chunk, 0, read);
            var responseBytes = buffer.ToArray();
            var headerLength = FindHeaderLength(responseBytes);
            if (headerLength < 0)
            {
                continue;
            }

            var parsed = Parse(responseBytes);
            var expectedLength = headerLength + 4 + parsed.Body.Length;
            if (responseBytes.Length >= expectedLength)
            {
                return parsed;
            }

            ReadExact(
                stream,
                parsed
                    .Body
                    .AsMemory(
                        responseBytes.Length - (headerLength + 4),
                        expectedLength - responseBytes.Length
                    )
            );
            return parsed;
        }
    }

    public static async Task<HttpResponseSnapshot> ReadAsync(NetworkStream stream)
    {
        using var buffer = new MemoryStream();

        while (true)
        {
            var chunk = new byte[256];
            var read = await stream.ReadAsync(chunk.AsMemory());
            if (read == 0)
            {
                throw new InvalidOperationException(
                    "Connection closed while reading HTTP response."
                );
            }

            buffer.Write(chunk, 0, read);
            var responseBytes = buffer.ToArray();
            var headerLength = FindHeaderLength(responseBytes);
            if (headerLength < 0)
            {
                continue;
            }

            var parsed = Parse(responseBytes);
            var expectedLength = headerLength + 4 + parsed.Body.Length;
            if (responseBytes.Length >= expectedLength)
            {
                return parsed;
            }

            var copiedBodyCount = responseBytes.Length - (headerLength + 4);
            await ReadExactAsync(
                stream,
                parsed.Body.AsMemory(copiedBodyCount, parsed.Body.Length - copiedBodyCount)
            );
            return parsed;
        }
    }

    private static int FindHeaderLength(byte[] buffer)
    {
        for (var index = 0; index <= buffer.Length - 4; index++)
        {
            if (
                buffer[index] == '\r'
                && buffer[index + 1] == '\n'
                && buffer[index + 2] == '\r'
                && buffer[index + 3] == '\n'
            )
            {
                return index;
            }
        }

        return -1;
    }

    private static void ReadExact(NetworkStream stream, Memory<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer.Span[offset..]);
            if (read == 0)
            {
                throw new InvalidOperationException(
                    "Connection closed while reading HTTP response body."
                );
            }

            offset += read;
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, Memory<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..]);
            if (read == 0)
            {
                throw new InvalidOperationException(
                    "Connection closed while reading HTTP response body."
                );
            }

            offset += read;
        }
    }
}

internal readonly record struct HttpResponseSnapshot(
    string StatusLine,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body
);
