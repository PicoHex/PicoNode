namespace PicoNode.Web.Internal;

internal sealed class RoutePattern
{
    private static readonly Dictionary<string, string> EmptyValues =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Segment[] _segments;

    private RoutePattern(Segment[] segments, bool hasParameters)
    {
        _segments = segments;
        IsExact = !hasParameters;
    }

    internal bool IsExact { get; }

    internal static RoutePattern Parse(string pattern)
    {
        if (pattern == "/")
        {
            return new RoutePattern([], false);
        }

        var parts = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var segments = new Segment[parts.Length];
        var hasParameters = false;

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];

            if (part.StartsWith('{') && part.EndsWith('}') && part.Length > 2)
            {
                segments[i] = new Segment(part[1..^1], isParameter: true);
                hasParameters = true;
            }
            else
            {
                segments[i] = new Segment(part, isParameter: false);
            }
        }

        return new RoutePattern(segments, hasParameters);
    }

    internal Dictionary<string, string>? Match(string path)
    {
        var span = path.AsSpan();
        Dictionary<string, string>? values = null;

        var segmentIndex = 0;
        var start = span.Length > 0 && span[0] == '/' ? 1 : 0;

        if (start >= span.Length)
        {
            return _segments.Length == 0 ? EmptyValues : null;
        }

        for (var i = start; i <= span.Length; i++)
        {
            if (i < span.Length && span[i] != '/')
            {
                continue;
            }

            if (segmentIndex >= _segments.Length)
            {
                return null;
            }

            var segment = span[start..i];
            var expected = _segments[segmentIndex];

            if (expected.IsParameter)
            {
                values ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                values[expected.Value] = Uri.UnescapeDataString(segment.ToString());
            }
            else if (!segment.SequenceEqual(expected.Value.AsSpan()))
            {
                return null;
            }

            segmentIndex++;
            start = i + 1;
        }

        if (segmentIndex != _segments.Length)
        {
            return null;
        }

        return values ?? EmptyValues;
    }

    private readonly struct Segment(string value, bool isParameter)
    {
        public string Value { get; } = value;
        public bool IsParameter { get; } = isParameter;
    }
}
