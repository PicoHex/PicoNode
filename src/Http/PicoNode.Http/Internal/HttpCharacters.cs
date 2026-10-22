namespace PicoNode.Http.Internal;

internal static class HttpCharacters
{
    public static bool IsHttpToken(string value) => IsHttpToken(value.AsSpan());

    public static bool IsHttpToken(ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!IsHttpTokenCharacter(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// RFC 9113 §8.2.1: an HTTP/2 field name is a token (RFC 9110 §5.6.2) that MUST be
    /// lowercase, so ONE pass answers both halves of the rule. A character that is
    /// not a <c>tchar</c> — a control octet (CR/LF/NUL/HTAB), SP, ':', or any
    /// non-ASCII character such as `Ä`/`ẞ`/Cyrillic `А` — and an ASCII uppercase
    /// letter are both rejected. Unlike <c>IndexOfAnyInRange('A','Z')</c> this is
    /// exact for every input, not just for ASCII.
    /// </summary>
    public static bool IsLowercaseHttpToken(ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsAsciiLetterUpper(character) || !IsHttpTokenCharacter(character))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsHttpToken(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var b in value)
        {
            if (!IsHttpTokenCharacter(b))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsValidHeaderValue(string value) => IsValidHeaderValue(value.AsSpan());

    /// <summary>
    /// RFC 9110 §5.5 field-value check over a char span, so a caller that already
    /// holds the value as a string never has to encode it into a scratch buffer
    /// first. The HTTP/2 request path calls this once per header field, and an
    /// <c>Encoding.ASCII.GetBytes</c> there would be pure per-request garbage.
    /// </summary>
    public static bool IsValidHeaderValue(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if ((character < 0x20 && character != '\t') || character == 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsValidHeaderValue(ReadOnlySpan<byte> value)
    {
        foreach (var b in value)
        {
            if ((b < 0x20 && b != (byte)'\t') || b == 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// RFC 9113 §8.2.1: a field value must not start or end with optional
    /// whitespace (SP / HTAB). HTTP/1.1 strips OWS in the request-line parser;
    /// HPACK carries a value verbatim, so HTTP/2 has to reject it here.
    /// </summary>
    public static bool HasLeadingOrTrailingWhitespace(ReadOnlySpan<char> value) =>
        value.Length > 0 && (value[0] is ' ' or '\t' || value[^1] is ' ' or '\t');

    public static bool IsHttpTokenCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character)
        || character
            is '!'
                or '#'
                or '$'
                or '%'
                or '&'
                or '\''
                or '*'
                or '+'
                or '-'
                or '.'
                or '^'
                or '_'
                or '`'
                or '|'
                or '~';

    public static bool IsHttpTokenCharacter(byte b) =>
        b
            is >= (byte)'A'
                and <= (byte)'Z'
                or >= (byte)'a'
                and <= (byte)'z'
                or >= (byte)'0'
                and <= (byte)'9'
                or (byte)'!'
                or (byte)'#'
                or (byte)'$'
                or (byte)'%'
                or (byte)'&'
                or (byte)'\''
                or (byte)'*'
                or (byte)'+'
                or (byte)'-'
                or (byte)'.'
                or (byte)'^'
                or (byte)'_'
                or (byte)'`'
                or (byte)'|'
                or (byte)'~';
}
