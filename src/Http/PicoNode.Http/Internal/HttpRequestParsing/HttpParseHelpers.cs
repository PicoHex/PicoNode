namespace PicoNode.Http.Internal.HttpRequestParsing;

internal static class HttpParseHelpers
{
    public static int ParseChunkSize(ReadOnlySpan<byte> line)
    {
        if (line.Length == 0)
        {
            return -1;
        }

        var semiColon = line.IndexOf((byte)';');
        var hexPart = semiColon >= 0 ? line[..semiColon] : line;

        if (hexPart.Length == 0)
        {
            return -1;
        }

        var size = 0;
        foreach (var b in hexPart)
        {
            if (!IsHexDigit(b))
            {
                return -1;
            }

            var digit = HexValue(b);
            if (size > ((int.MaxValue - digit) >> 4))
            {
                return -1;
            }

            size = (size << 4) | digit;
        }

        return size;
    }

    public static int HexValue(byte b) =>
        b switch
        {
            >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
            >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
            >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
            _ => -1,
        };

    public static ReadOnlySpan<byte> TrimOws(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && IsOws(value[start]))
        {
            start++;
        }

        var end = value.Length;
        while (end > start && IsOws(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    public static bool IsOws(byte b) => b is (byte)' ' or (byte)'\t';

    public static bool IsHexDigit(byte b) =>
        b
            is >= (byte)'0'
                and <= (byte)'9'
                or >= (byte)'A'
                and <= (byte)'F'
                or >= (byte)'a'
                and <= (byte)'f';

    public static byte GetFirstByte(ReadOnlySequence<byte> sequence) => sequence.FirstSpan[0];

    public static byte GetLastByte(ReadOnlySequence<byte> sequence) =>
        sequence.Slice(sequence.Length - 1).FirstSpan[0];
}
