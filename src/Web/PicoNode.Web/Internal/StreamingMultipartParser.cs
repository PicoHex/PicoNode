namespace PicoNode.Web.Internal;

internal static class StreamingMultipartParser
{
    public static async ValueTask<MultipartFormData?> ParseAsync(
        Stream bodyStream,
        string boundaryName,
        int bufferSize = 65536,
        int maxPartSizeBytes = MultipartFormDataParserOptions.DefaultMaxPartSizeBytes,
        int maxTotalSizeBytes = MultipartFormDataParserOptions.DefaultMaxTotalSizeBytes,
        int maxPartCount = MultipartFormDataParserOptions.DefaultMaxPartCount,
        int maxHeaderBytesPerPart = MultipartFormDataParserOptions.DefaultMaxHeaderBytesPerPart,
        CancellationToken ct = default
    )
    {
        var boundaryBytes = Encoding.ASCII.GetBytes(boundaryName);
        using var reader = new MultipartBufferedReader(bodyStream, bufferSize);

        // Skip the first boundary (--boundary)
        var first = await reader.ReadUntilBoundaryAsync(boundaryBytes, maxBytes: 0, ct);
        if (first is null)
            return null;

        var fields = new List<MultipartFormField>();
        var files = new List<MultipartFormFile>();
        long totalBytes = 0;
        var partCount = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var part = await MultipartPartReader.ReadPartAsync(
                reader,
                boundaryBytes,
                maxPartSizeBytes,
                maxHeaderBytesPerPart,
                ct
            );
            if (part is null)
                break;

            // Same limits as the in-memory path, in the same order: part count, the
            // running total (headers + content) after each addition, then the per-part
            // content guard (which MultipartPartReader applied while accumulating).
            partCount++;
            if (partCount > maxPartCount)
            {
                throw new InvalidDataException("Multipart body exceeds maximum part count");
            }

            totalBytes += part.HeaderBytes;
            if (totalBytes > maxTotalSizeBytes)
            {
                throw new InvalidDataException("Multipart body exceeds maximum total size");
            }

            if (part.Content is not null)
            {
                totalBytes += part.Content.Length;
                if (totalBytes > maxTotalSizeBytes)
                {
                    throw new InvalidDataException("Multipart body exceeds maximum total size");
                }
            }

            if (!part.Headers.TryGetValue("Content-Disposition", out var disposition))
                continue;

            var name = ContentDispositionHeader.GetParameter(disposition, "name");
            if (name is null)
                continue;

            var fileName = ContentDispositionHeader.GetParameter(disposition, "filename");

            if (fileName is not null)
            {
                var contentType = part.Headers.TryGetValue("Content-Type", out var contentTypeVal)
                    ? contentTypeVal
                    : "application/octet-stream";
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
}
