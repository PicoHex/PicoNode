namespace PicoNode.Http.Internal;

internal static class HttpCharacters
{
    public static bool IsHttpToken(string value)
    {
        return value.Length != 0 && value.All(IsHttpTokenCharacter);
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
