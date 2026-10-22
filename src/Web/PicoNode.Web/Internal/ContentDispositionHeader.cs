namespace PicoNode.Web.Internal;

/// <summary>
/// Reads one parameter out of a <c>Content-Disposition</c> header value
/// (<c>form-data; name="f"; filename="a.txt"</c>).
/// <para>
/// The value is split on <c>;</c> characters that sit outside a quoted string, and a
/// parameter name has to match a whole token before its <c>=</c>, so <c>name=</c> can
/// never be found inside <c>filename=</c>. Both multipart parse paths call this single
/// implementation: a private copy per path is exactly how they drifted apart before
/// (audit F-45 reported the same unanchored <c>IndexOf</c> in both).
/// </para>
/// </summary>
internal static class ContentDispositionHeader
{
    /// <summary>
    /// Returns the value of <paramref name="paramName"/>, or <c>null</c> when the header
    /// carries no such parameter (or carries an unterminated quoted value).
    /// </summary>
    public static string? GetParameter(string header, string paramName)
    {
        var remaining = header.AsSpan();
        var name = paramName.AsSpan();

        while (!remaining.IsEmpty)
        {
            var separator = IndexOfUnquoted(remaining, ';');
            var segment = separator < 0 ? remaining : remaining[..separator];
            remaining = separator < 0 ? default : remaining[(separator + 1)..];

            // A parameter is `token=value`, and the first '=' is that delimiter: a
            // parameter name is an HTTP token, so it cannot contain one.
            var equals = segment.IndexOf('=');
            if (equals <= 0)
                continue;

            if (!segment[..equals].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            return ReadValue(segment[(equals + 1)..].Trim());
        }

        return null;
    }

    /// <summary>Index of the first <paramref name="separator"/> outside a quoted string.</summary>
    private static int IndexOfUnquoted(ReadOnlySpan<char> value, char separator)
    {
        var quoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quoted)
            {
                if (c == '\\')
                    i++; // quoted-pair: the escaped character is literal
                else if (c == '"')
                    quoted = false;
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == separator)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Turns a parameter value into text: a quoted string loses its quotes and resolves
    /// quoted-pairs, anything else is taken verbatim (it was already trimmed and cannot
    /// run past the next unquoted <c>;</c>).
    /// </summary>
    private static string? ReadValue(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value[0] != '"')
            return value.ToString();

        var end = FindClosingQuote(value);
        if (end < 0)
            return null; // unterminated quote: report "no parameter" rather than guess

        var inner = value[1..end];
        if (inner.IndexOf('\\') < 0)
            return inner.ToString();

        var builder = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
                i++;
            builder.Append(inner[i]);
        }

        return builder.ToString();
    }

    private static int FindClosingQuote(ReadOnlySpan<char> value)
    {
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\')
                i++;
            else if (c == '"')
                return i;
        }

        return -1;
    }
}
