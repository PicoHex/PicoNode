using System.Globalization;

namespace PicoNode.Http.Internal;

internal static class HostValidator
{
    public static bool IsValidHostHeaderValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character is ',' or '/' or '?' or '#' or '@')
            {
                return false;
            }
        }

        if (value.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> span = value;

        if (span[0] == '[')
        {
            return TryParseBracketedIpv6Host(span);
        }

        var lastColon = span.LastIndexOf(':');

        if (lastColon >= 0)
        {
            var hostPart = span[..lastColon];
            var portPart = span[(lastColon + 1)..];

            if (hostPart.Length == 0 || !IsValidPort(portPart))
            {
                return false;
            }

            return IsValidHostName(hostPart) || IsValidIpv4Address(hostPart);
        }

        return IsValidHostName(span) || IsValidIpv4Address(span);
    }

    private static bool TryParseBracketedIpv6Host(ReadOnlySpan<char> value)
    {
        var closingBracketIndex = value.IndexOf(']');
        if (closingBracketIndex <= 1)
        {
            return false;
        }

        var addressPart = value[1..closingBracketIndex];
        if (!System.Net.IPAddress.TryParse(addressPart, out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (closingBracketIndex == value.Length - 1)
        {
            return true;
        }

        if (value[closingBracketIndex + 1] != ':')
        {
            return false;
        }

        return IsValidPort(value[(closingBracketIndex + 2)..]);
    }

    private static bool IsValidPort(ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is >= 0 and <= 65535;
    }

    private static bool IsValidHostName(ReadOnlySpan<char> value)
    {
        if (value.Length == 0 || value[^1] == '.')
        {
            return false;
        }

        while (value.Length > 0)
        {
            var dotIndex = value.IndexOf('.');
            var label = dotIndex >= 0 ? value[..dotIndex] : value;

            if (label.Length == 0)
            {
                return false;
            }

            if (!char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1]))
            {
                return false;
            }

            foreach (var character in label)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '-')
                {
                    return false;
                }
            }

            if (dotIndex < 0)
            {
                break;
            }

            value = value[(dotIndex + 1)..];
        }

        return true;
    }

    private static bool IsValidIpv4Address(ReadOnlySpan<char> value)
    {
        var segmentCount = 0;

        while (value.Length > 0)
        {
            var dotIndex = value.IndexOf('.');
            var segment = dotIndex >= 0 ? value[..dotIndex] : value;

            if (segment.Length == 0
                || !byte.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }

            segmentCount++;

            if (dotIndex < 0)
            {
                break;
            }

            value = value[(dotIndex + 1)..];
        }

        return segmentCount == 4;
    }
}
