namespace PicoNode.Web;

public sealed class MultipartFormData
{
    internal MultipartFormData(
        IReadOnlyList<MultipartFormField> fields,
        IReadOnlyList<MultipartFormFile> files
    )
    {
        Fields = fields;
        Files = files;
    }

    public IReadOnlyList<MultipartFormField> Fields { get; }

    public IReadOnlyList<MultipartFormFile> Files { get; }
}

public sealed class MultipartFormField
{
    internal MultipartFormField(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }

    public string Value { get; }
}

public sealed class MultipartFormFile
{
    private readonly Stream? _contentStream;
    private readonly ReadOnlyMemory<byte> _content;

    internal MultipartFormFile(
        string name,
        string fileName,
        string contentType,
        ReadOnlyMemory<byte> content
    )
        : this(name, fileName, contentType)
    {
        _content = content;
        Length = content.Length;
    }

    internal MultipartFormFile(
        string name,
        string fileName,
        string contentType,
        Stream contentStream,
        long length
    )
        : this(name, fileName, contentType)
    {
        _contentStream = contentStream ?? throw new ArgumentNullException(nameof(contentStream));
        Length = length;
    }

    private MultipartFormFile(string name, string fileName, string contentType)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        ContentType = contentType ?? throw new ArgumentNullException(nameof(contentType));
        SafeFileName = SanitizeFileName(FileName);
    }

    public string Name { get; }

    /// <summary>
    /// File name exactly as the client sent it. It is attacker-controlled text: never
    /// join it onto a directory, use <see cref="SafeFileName"/> for that.
    /// </summary>
    public string FileName { get; }

    public string ContentType { get; }

    /// <summary>File size in bytes.</summary>
    public long Length { get; }

    /// <summary>
    /// <see cref="FileName"/> reduced to one plain path segment, computed once at
    /// construction. Directories are dropped (both <c>/</c> and <c>\</c> — <c>\</c> is
    /// not a separator to <see cref="Path.GetFileName(string)"/> on Unix, so
    /// <c>..\..\win.ini</c> would survive it), control characters and <c>:</c> are
    /// removed (a drive-relative <c>C:evil.txt</c> escapes the upload directory and
    /// <c>a:stream</c> names an NTFS alternate data stream), and trailing spaces/dots are
    /// trimmed. When nothing usable is left (<c>""</c>, <c>"."</c>, <c>".."</c>, a name made
    /// of separators only) this is <see cref="string.Empty"/> — the name is refused rather
    /// than guessed at, so a caller must treat empty as "no usable file name".
    /// </summary>
    public string SafeFileName { get; }

    /// <summary>In-memory file content (only available when Length ≤ buffer threshold).</summary>
    public ReadOnlyMemory<byte> Content =>
        _contentStream is null ? _content : ReadOnlyMemory<byte>.Empty;

    /// <summary>Opens a read-only stream for reading the file content.</summary>
    public Stream OpenReadStream() =>
        _contentStream ?? new MemoryStream(_content.ToArray(), writable: false);

    private static string SanitizeFileName(string fileName)
    {
        var span = fileName.AsSpan();

        // A drive qualifier ("C:evil.txt") resolves against that drive's current
        // directory, so it is not a path segment at all — drop it before the split.
        if (span.Length >= 2 && span[1] == ':')
            span = span[2..];

        var separator = span.LastIndexOfAny('\\', '/');
        if (separator >= 0)
            span = span[(separator + 1)..];

        // Only pay for a copy when something actually has to be dropped.
        StringBuilder? builder = null;
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (char.IsControl(c) || c == ':')
            {
                builder ??= new StringBuilder(span.Length).Append(span[..i]);
                continue;
            }

            builder?.Append(c);
        }

        var kept = builder is null ? span : builder.ToString().AsSpan();

        // Windows drops trailing spaces and dots, so ".." and "a/.." arrive here as dots
        // to strip; doing it here also refuses ".." and "." without a special case.
        var end = kept.Length;
        while (end > 0 && (kept[end - 1] == ' ' || kept[end - 1] == '.'))
            end--;
        kept = kept[..end];

        return kept.IsEmpty ? string.Empty : kept.ToString();
    }
}
