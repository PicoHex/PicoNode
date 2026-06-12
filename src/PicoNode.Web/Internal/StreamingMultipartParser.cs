namespace PicoNode.Web.Internal;

internal static class StreamingMultipartParser
{
    public static async ValueTask<MultipartFormData?> ParseAsync(
        Stream bodyStream, string boundaryName, int bufferSize = 65536)
    {
        var boundary = Encoding.ASCII.GetBytes(boundaryName);
        using var reader = new MultipartBufferedReader(bodyStream, bufferSize);

        // Skip the first boundary (--boundary)
        var first = await reader.ReadUntilBoundaryAsync(boundary);
        if (first is null)
            return null;

        var fields = new List<MultipartFormField>();
        var files = new List<MultipartFormFile>();

        while (true)
        {
            var part = await MultipartPartReader.ReadPartAsync(reader, boundary);
            if (part is null)
                break;

            if (!part.Headers.TryGetValue("Content-Disposition", out var disposition))
                continue;

            var name = ExtractParameter(disposition, "name");
            if (name is null)
                continue;

            var fileName = ExtractParameter(disposition, "filename");

            if (fileName is not null)
            {
                var contentType = part.Headers.TryGetValue("Content-Type", out var ct)
                    ? ct : "application/octet-stream";
                var content = part.Content ?? [];
                files.Add(new MultipartFormFile(name, fileName, contentType, content));
            }
            else
            {
                var text = part.Content is not null
                    ? Encoding.UTF8.GetString(part.Content)
                    : string.Empty;
                fields.Add(new MultipartFormField(name, text));
            }
        }

        return new MultipartFormData(fields, files);
    }

    private static string? ExtractParameter(string header, string paramName)
    {
        var searchKey = paramName + "=";
        var span = header.AsSpan();
        var idx = span.IndexOf(searchKey, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        return ExtractValue(span[(idx + searchKey.Length)..]);
    }

    private static string? ExtractValue(ReadOnlySpan<char> value)
    {
        if (value.Length >= 2 && value[0] == '"')
        {
            var endQuote = value[1..].IndexOf('"');
            return endQuote < 0 ? null : value[1..(endQuote + 1)].ToString();
        }
        var end = value.IndexOfAny([';', ' ', '\r', '\n']);
        return (end >= 0 ? value[..end] : value).ToString();
    }
}
