namespace PicoNode.Http.Internal.HttpRequestParsing;

internal static class HttpRequestLineParser
{
    private static readonly SearchValues<byte> InvalidTargetBytes = SearchValues.Create(
        " \t\r\n#\\"u8
    );

    public static bool TryParse(
        ReadOnlySpan<byte> line,
        out string method,
        out string target,
        out string version
    )
    {
        method = string.Empty;
        target = string.Empty;
        version = string.Empty;

        var firstSpace = line.IndexOf((byte)' ');
        if (firstSpace <= 0)
        {
            return false;
        }

        var rest = line[(firstSpace + 1)..];
        var secondSpace = rest.IndexOf((byte)' ');
        if (secondSpace <= 0)
        {
            return false;
        }

        var versionSpan = rest[(secondSpace + 1)..];
        if (versionSpan.IndexOf((byte)' ') >= 0)
        {
            return false;
        }

        var methodSpan = line[..firstSpace];
        var targetSpan = rest[..secondSpace];

        if (!HttpCharacters.IsHttpToken(methodSpan) || !IsRequestTarget(targetSpan))
        {
            return false;
        }

        if (versionSpan.SequenceEqual("HTTP/1.1"u8))
        {
            version = "HTTP/1.1";
        }
        else if (versionSpan.SequenceEqual("HTTP/1.0"u8))
        {
            version = "HTTP/1.0";
        }
        else
        {
            return false;
        }

        method = LookupKnownMethod(methodSpan) ?? Encoding.ASCII.GetString(methodSpan);
        target = Encoding.ASCII.GetString(targetSpan);
        return true;
    }

    private static string? LookupKnownMethod(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
            return null;
        return span[0] switch
        {
            (byte)'G' => span.SequenceEqual("GET"u8) ? "GET" : null,
            (byte)'P'
                => span.Length switch
                {
                    3 when span.SequenceEqual("PUT"u8) => "PUT",
                    4 when span.SequenceEqual("POST"u8) => "POST",
                    5 when span.SequenceEqual("PATCH"u8) => "PATCH",
                    _ => null,
                },
            (byte)'D' => span.SequenceEqual("DELETE"u8) ? "DELETE" : null,
            (byte)'H' => span.SequenceEqual("HEAD"u8) ? "HEAD" : null,
            (byte)'O' => span.SequenceEqual("OPTIONS"u8) ? "OPTIONS" : null,
            (byte)'T' => span.SequenceEqual("TRACE"u8) ? "TRACE" : null,
            (byte)'C' => span.SequenceEqual("CONNECT"u8) ? "CONNECT" : null,
            _ => null,
        };
    }

    private static bool IsRequestTarget(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0 || value[0] != (byte)'/')
        {
            return false;
        }

        if (value.IndexOfAny(InvalidTargetBytes) >= 0)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] < 0x20 || value[index] >= 0x7F)
            {
                return false;
            }

            if (value[index] != (byte)'%')
            {
                continue;
            }

            if (
                index + 2 >= value.Length
                || !HttpParseHelpers.IsHexDigit(value[index + 1])
                || !HttpParseHelpers.IsHexDigit(value[index + 2])
            )
            {
                return false;
            }

            index += 2;
        }

        return true;
    }
}
