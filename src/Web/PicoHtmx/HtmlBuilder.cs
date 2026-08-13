using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace PicoHtmx;

public static class H
{
    public static string E(string? raw) =>
        raw is null
            ? ""
            : raw.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");

    // ── Core (generic attrs) ──────────────────────────────────────────────
    // Generic attrs carry [DynamicallyAccessedMembers(PublicProperties)]: the
    // trimmer preserves the attrs type's public properties at every call site
    // (anonymous types included), so the reflective AppendAttributes below is
    // AOT-safe — a bare `object` param would let trimming strip those
    // properties and every attribute would silently vanish from the HTML.

    public static string Tag<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T
    >(string name, string? content = null, T? attrs = null)
        where T : class
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

    // Non-generic fallback for null / runtime-dynamic attrs. Passing a non-null
    // object-typed attrs bag FAILS LOUDLY: reflection cannot see its properties
    // under PublishAot trimming (DAM only flows through the generic parameter),
    // which would silently render empty attributes. Use the generic overloads.
    public static string Tag(string name, string? content = null, object? attrs = null)
    {
        ThrowIfObjectTypedAttrs(attrs);
        return Tag<object>(name, content, attrs);
    }

    private static void ThrowIfObjectTypedAttrs(object? attrs)
    {
        if (attrs is not null)
        {
            throw new NotSupportedException(
                "Attribute objects must be passed through the generic overloads "
                    + "(e.g. H.Tag<T>(...)) so their properties survive trimming "
                    + "([DynamicallyAccessedMembers] only flows through the generic "
                    + "parameter). Object-typed attrs would silently render empty "
                    + "attributes under PublishAot."
            );
        }
    }

    public static string Div<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T
    >(string? content = null, T? attrs = null)
        where T : class => Tag("div", content, attrs);

    public static string Div(string? content = null, object? attrs = null)
    {
        ThrowIfObjectTypedAttrs(attrs);
        return Tag<object>("div", content, attrs);
    }

    public static string Span<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T
    >(string? content = null, T? attrs = null)
        where T : class => Tag("span", content, attrs);

    public static string Span(string? content = null, object? attrs = null)
    {
        ThrowIfObjectTypedAttrs(attrs);
        return Tag<object>("span", content, attrs);
    }

    public static string P<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T
    >(string text, T? attrs = null)
        where T : class => Tag("p", E(text), attrs);

    public static string P(string text, object? attrs = null)
    {
        ThrowIfObjectTypedAttrs(attrs);
        return Tag<object>("p", E(text), attrs);
    }

    public static string Button<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T
    >(string text, T? attrs = null)
        where T : class => Tag("button", E(text), attrs);

    public static string Button(string text, object? attrs = null)
    {
        ThrowIfObjectTypedAttrs(attrs);
        return Tag<object>("button", E(text), attrs);
    }

    public static string A<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T
    >(string text, string href, T? attrs = null)
        where T : class
    {
        var attrStr = BuildAttrString(attrs);
        return $"<a href=\"{E(href)}\"{attrStr}>{E(text)}</a>";
    }

    public static string A(string text, string href, object? attrs = null)
    {
        ThrowIfObjectTypedAttrs(attrs);
        var attrStr = BuildAttrString(attrs);
        return $"<a href=\"{E(href)}\"{attrStr}>{E(text)}</a>";
    }

    public static string Input<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T
    >(T? attrs = null)
        where T : class => Tag("input", null, attrs);

    public static string Input(object? attrs = null)
    {
        ThrowIfObjectTypedAttrs(attrs);
        return Tag<object>("input", null, attrs);
    }

    public static string TextArea<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T
    >(string? value = null, T? attrs = null)
        where T : class => Tag("textarea", E(value), attrs);

    public static string TextArea(string? value = null, object? attrs = null)
    {
        ThrowIfObjectTypedAttrs(attrs);
        return Tag<object>("textarea", E(value), attrs);
    }

    public static string Script(string src) => Tag("script", null, new { src });

    public static string Link(string href, string rel = "stylesheet") =>
        Tag("link", null, new { href, rel });

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:GetProperties",
        Justification = "Property preservation is guaranteed by the generic attrs parameter's "
            + "[DynamicallyAccessedMembers(PublicProperties)] constraint at every call site; "
            + "the non-generic fallback accepts only null or caller-preserved types."
    )]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:GetValue",
        Justification = "Property preservation is guaranteed by the generic attrs parameter's "
            + "[DynamicallyAccessedMembers(PublicProperties)] constraint at every call site; "
            + "the non-generic fallback accepts only null or caller-preserved types."
    )]
    private static void AppendAttributes(StringBuilder sb, object? attrs)
    {
        if (attrs is null)
            return;
        var attrType = attrs.GetType();
#pragma warning disable IL2075 // Attribute properties preserved via the generic attrs DAM constraint
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
                    _ => prop.Name.Replace('_', '-'),
                };
                sb.Append(' ').Append(name).Append("=\"").Append(E(val)).Append('"');
            }
        }
    }

    private static string BuildAttrString(object? attrs)
    {
        if (attrs is null)
            return "";
        var sb = new StringBuilder();
        AppendAttributes(sb, attrs);
        return sb.ToString();
    }
}
