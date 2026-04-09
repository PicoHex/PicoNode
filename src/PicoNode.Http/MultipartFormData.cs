namespace PicoNode.Http;

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
    internal MultipartFormFile(
        string name,
        string fileName,
        string contentType,
        ReadOnlyMemory<byte> content
    )
    {
        Name = name;
        FileName = fileName;
        ContentType = contentType;
        Content = content;
    }

    public string Name { get; }

    public string FileName { get; }

    public string ContentType { get; }

    public ReadOnlyMemory<byte> Content { get; }
}
