namespace PicoNode.Web;

public static class MultipartFormDataParser
{
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();
    private static readonly byte[] DoubleCrLf = "\r\n\r\n"u8.ToArray();
    private static readonly byte[] DashDash = "--"u8.ToArray();

    public static MultipartFormData? Parse(HttpRequest request)
    {
        var contentType = GetHeaderValue(request.HeaderFields, "Content-Type");
        if (contentType is null)
            return null;

        if (!contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return null;

        var boundary = ExtractBoundary(contentType);
        return boundary is null
            ? null
            : ParseBody(request.Body, Encoding.UTF8.GetBytes(boundary));
    }

    internal static string? ExtractBoundary(string contentType)
    {
        var span = contentType.AsSpan();
        var idx = span.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        var value = span[(idx + 9)..];

        if (value.Length >= 2 && value[0] == '"')
        {
            var endQuote = value[1..].IndexOf('"');
            return endQuote < 0 ? null : value[1..(endQuote + 1)].ToString();
        }

        var end = value.IndexOfAny(';', ' ');
        return (end >= 0 ? value[..end] : value).ToString();
    }

    private static MultipartFormData ParseBody(ReadOnlyMemory<byte> body, byte[] boundary)
    {
        var bodySpan = body.Span;
        var fields = new List<MultipartFormField>();
        var files = new List<MultipartFormFile>();

        var delimiter = new byte[boundary.Length + 2];
        DashDash.CopyTo(delimiter.AsSpan());
        boundary.CopyTo(delimiter.AsSpan(2));

        var pos = IndexOf(bodySpan, delimiter);
        if (pos < 0)
            return new MultipartFormData(fields, files);

        pos += delimiter.Length;

        while (pos < bodySpan.Length)
        {
            if (pos + CrLf.Length <= bodySpan.Length && bodySpan[pos..].StartsWith(DashDash))
                break;

            if (pos + CrLf.Length <= bodySpan.Length && bodySpan[pos..].StartsWith(CrLf))
                pos += CrLf.Length;
            else
                break;

            var headerEnd = IndexOf(bodySpan[pos..], DoubleCrLf);
            if (headerEnd < 0)
                break;

            var headerBytes = bodySpan[pos..(pos + headerEnd)];
            pos += headerEnd + DoubleCrLf.Length;

            var nextDelimiter = IndexOf(bodySpan[pos..], delimiter);
            if (nextDelimiter < 0)
                break;

            var contentLength = nextDelimiter - CrLf.Length;
            if (contentLength < 0)
                break;

            var content = body.Slice(pos, contentLength);
            pos += nextDelimiter + delimiter.Length;

            ParsePart(headerBytes, content, fields, files);
        }

        return new MultipartFormData(fields, files);
    }

    private static void ParsePart(
        ReadOnlySpan<byte> headerBytes,
        ReadOnlyMemory<byte> content,
        List<MultipartFormField> fields,
        List<MultipartFormFile> files
    )
    {
        var headers = Encoding.UTF8.GetString(headerBytes);
        var disposition = GetPartHeaderValue(headers, "Content-Disposition");
        if (disposition is null)
            return;

        var name = ExtractParameter(disposition, "name");
        if (name is null)
            return;

        var fileName = ExtractParameter(disposition, "filename");

        if (fileName is not null)
        {
            var ct = GetPartHeaderValue(headers, "Content-Type") ?? "application/octet-stream";
            files.Add(new MultipartFormFile(name, fileName, ct, content));
        }
        else
        {
            fields.Add(new MultipartFormField(name, Encoding.UTF8.GetString(content.Span)));
        }
    }

    private static string? ExtractParameter(string header, string paramName)
    {
        var searchKey = paramName + "=";
        var span = header.AsSpan();
        var idx = span.IndexOf(searchKey, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        var value = span[(idx + searchKey.Length)..];

        if (value.Length >= 2 && value[0] == '"')
        {
            var endQuote = value[1..].IndexOf('"');
            return endQuote < 0 ? null : value[1..(endQuote + 1)].ToString();
        }

        var end = value.IndexOfAny([';', ' ', '\r', '\n']);
        return (end >= 0 ? value[..end] : value).ToString();
    }

    private static string? GetPartHeaderValue(string headers, string headerName)
    {
        return (
            from line in headers.Split("\r\n")
            let colonIdx = line.IndexOf(':')
            where colonIdx > 0
            let key = line[..colonIdx].Trim()
            where key.Equals(headerName, StringComparison.OrdinalIgnoreCase)
            select line[(colonIdx + 1)..].Trim()
        ).FirstOrDefault();
    }

    private static string? GetHeaderValue(
        IReadOnlyList<KeyValuePair<string, string>> headers,
        string name
    )
    {
        return (
            from header in headers
            where header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
            select header.Value
        ).FirstOrDefault();
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        return haystack.IndexOf(needle);
    }
}
