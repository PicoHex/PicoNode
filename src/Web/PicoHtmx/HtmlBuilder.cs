namespace PicoHtmx;

public static class H
{
    public static string E(string? raw) =>
        raw is null ? "" : raw
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");

    public static string Tag(string name, string? content = null, object? attrs = null)
    {
        var sb = new StringBuilder();
        sb.Append('<').Append(name);
        AppendAttributes(sb, attrs);
        if (content is null)
            sb.Append(" />");
        else
            sb.Append('>').Append(content).Append("</").Append(name).Append('>');
        return sb.ToString();
    }

    public static string Div(string? content = null, object? attrs = null) =>
        Tag("div", content, attrs);

    public static string Span(string? content = null, object? attrs = null) =>
        Tag("span", content, attrs);

    public static string P(string text, object? attrs = null) =>
        Tag("p", E(text), attrs);

    public static string Button(string text, object? attrs = null) =>
        Tag("button", E(text), attrs);

    public static string A(string text, string href, object? attrs = null)
    {
        var attrStr = BuildAttrString(attrs);
        return $"<a href=\"{E(href)}\"{attrStr}>{E(text)}</a>";
    }

    public static string Input(object? attrs = null) =>
        Tag("input", null, attrs);

    public static string TextArea(string? value = null, object? attrs = null) =>
        Tag("textarea", E(value), attrs);

    public static string Script(string src) =>
        Tag("script", null, new { src });

    public static string Link(string href, string rel = "stylesheet") =>
        Tag("link", null, new { href, rel });

    private static void AppendAttributes(StringBuilder sb, object? attrs)
    {
        if (attrs is null) return;
        var attrType = attrs.GetType();
#pragma warning disable IL2075 // Reflection on anonymous types is intentional and NativeAOT-safe
        foreach (var prop in attrType.GetProperties())
#pragma warning restore IL2075
        {
            var val = prop.GetValue(attrs)?.ToString();
            if (val is not null)
            {
                var name = prop.Name switch
                {
                    "class" => "class",
                    "@class" => "class",
                    _ => prop.Name.Replace('_', '-')
                };
                sb.Append(' ').Append(name).Append("=\"").Append(E(val)).Append('"');
            }
        }
    }

    private static string BuildAttrString(object? attrs)
    {
        if (attrs is null) return "";
        var sb = new StringBuilder();
        AppendAttributes(sb, attrs);
        return sb.ToString();
    }
}
