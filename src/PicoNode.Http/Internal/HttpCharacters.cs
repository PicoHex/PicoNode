namespace PicoNode.Http.Internal;

internal static class HttpCharacters
{
    public static bool IsHttpToken(string value)
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

    public static bool IsValidHeaderValue(string value)
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

    public static bool IsHttpTokenCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character)
        || character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    public static bool IsHttpTokenCharacter(byte b) =>
        b is >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'a' and <= (byte)'z'
            or >= (byte)'0' and <= (byte)'9'
            or (byte)'!' or (byte)'#' or (byte)'$' or (byte)'%' or (byte)'&'
            or (byte)'\'' or (byte)'*' or (byte)'+' or (byte)'-' or (byte)'.'
            or (byte)'^' or (byte)'_' or (byte)'`' or (byte)'|' or (byte)'~';
}
