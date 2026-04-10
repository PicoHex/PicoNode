using System.Text;

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
        if (boundary is null)
            return null;

        return ParseBody(request.Body.Span, Encoding.UTF8.GetBytes(boundary));
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
            if (endQuote < 0)
                return null;
            return value[1..(endQuote + 1)].ToString();
        }

        var end = value.IndexOfAny(';', ' ');
        return (end >= 0 ? value[..end] : value).ToString();
    }

    private static MultipartFormData ParseBody(ReadOnlySpan<byte> body, byte[] boundary)
    {
        var fields = new List<MultipartFormField>();
        var files = new List<MultipartFormFile>();

        var delimiter = new byte[boundary.Length + 2];
        DashDash.CopyTo(delimiter.AsSpan());
        boundary.CopyTo(delimiter.AsSpan(2));

        var closeDelimiter = new byte[delimiter.Length + 2];
        delimiter.CopyTo(closeDelimiter.AsSpan());
        DashDash.CopyTo(closeDelimiter.AsSpan(delimiter.Length));

        var pos = IndexOf(body, delimiter);
        if (pos < 0)
            return new MultipartFormData(fields, files);

        pos += delimiter.Length;

        while (pos < body.Length)
        {
            if (pos + CrLf.Length <= body.Length && body[pos..].StartsWith(DashDash))
                break;

            if (pos + CrLf.Length <= body.Length && body[pos..].StartsWith(CrLf))
                pos += CrLf.Length;
            else
                break;

            var headerEnd = IndexOf(body[pos..], DoubleCrLf);
            if (headerEnd < 0)
                break;

            var headerBytes = body[pos..(pos + headerEnd)];
            pos += headerEnd + DoubleCrLf.Length;

            var nextDelimiter = IndexOf(body[pos..], delimiter);
            if (nextDelimiter < 0)
                break;

            var content = body[pos..(pos + nextDelimiter - CrLf.Length)];
            pos += nextDelimiter + delimiter.Length;

            ParsePart(headerBytes, content, fields, files);
        }

        return new MultipartFormData(fields, files);
    }

    private static void ParsePart(
        ReadOnlySpan<byte> headerBytes,
        ReadOnlySpan<byte> content,
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
            files.Add(new MultipartFormFile(name, fileName, ct, content.ToArray()));
        }
        else
        {
            fields.Add(new MultipartFormField(name, Encoding.UTF8.GetString(content)));
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
            if (endQuote < 0)
                return null;
            return value[1..(endQuote + 1)].ToString();
        }

        var end = value.IndexOfAny([';', ' ', '\r', '\n']);
        return (end >= 0 ? value[..end] : value).ToString();
    }

    private static string? GetPartHeaderValue(string headers, string headerName)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0)
                continue;

            var key = line[..colonIdx].Trim();
            if (key.Equals(headerName, StringComparison.OrdinalIgnoreCase))
                return line[(colonIdx + 1)..].Trim();
        }

        return null;
    }

    private static string? GetHeaderValue(
        IReadOnlyList<KeyValuePair<string, string>> headers,
        string name
    )
    {
        foreach (var header in headers)
        {
            if (header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return header.Value;
        }

        return null;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        return haystack.IndexOf(needle);
    }
}
