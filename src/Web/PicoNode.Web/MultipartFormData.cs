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
    }

    public string Name { get; }

    public string FileName { get; }

    public string ContentType { get; }

    /// <summary>File size in bytes.</summary>
    public long Length { get; }

    /// <summary>In-memory file content (only available when Length ≤ buffer threshold).</summary>
    public ReadOnlyMemory<byte> Content =>
        _contentStream is null ? _content : ReadOnlyMemory<byte>.Empty;

    /// <summary>Opens a read-only stream for reading the file content.</summary>
    public Stream OpenReadStream() =>
        _contentStream ?? new MemoryStream(_content.ToArray(), writable: false);
}
