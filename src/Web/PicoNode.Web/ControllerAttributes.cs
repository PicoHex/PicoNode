namespace PicoNode.Web;

/// <summary>
/// Attribute stubs for Controllers.Gen (matched by class name, not namespace).
/// The generator checks AttributeClass.Name, so class name is all that matters.
/// </summary>

[AttributeUsage(AttributeTargets.Class)]
public sealed class RouteAttribute : Attribute
{
    public string Path { get; }
    public RouteAttribute(string path) => Path = path;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class HttpGetAttribute : Attribute
{
    public string Path { get; }
    public HttpGetAttribute(string path = "") => Path = path;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class HttpPostAttribute : Attribute
{
    public string Path { get; }
    public HttpPostAttribute(string path = "") => Path = path;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class HttpPutAttribute : Attribute
{
    public string Path { get; }
    public HttpPutAttribute(string path = "") => Path = path;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class HttpDeleteAttribute : Attribute
{
    public string Path { get; }
    public HttpDeleteAttribute(string path = "") => Path = path;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class HttpPatchAttribute : Attribute
{
    public string Path { get; }
    public HttpPatchAttribute(string path = "") => Path = path;
}
