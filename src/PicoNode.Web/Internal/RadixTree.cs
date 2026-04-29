namespace PicoNode.Web.Internal;

internal sealed class RadixTree<T>
{
    private readonly Node _root = new();

    public void Insert(string pattern, T value)
    {
        Insert(pattern, string.Empty, value);
    }

    public void Insert(string pattern, string method, T value)
    {
        var node = _root;
        var segments = SplitPath(pattern);

        foreach (var segment in segments)
        {
            if (IsParameter(segment, out var paramName))
            {
                node.ParamChild ??= new Node();
                node.ParamName ??= paramName;
                node = node.ParamChild;
            }
            else
            {
                node.Children ??= new Dictionary<string, Node>(StringComparer.Ordinal);
                if (!node.Children.TryGetValue(segment, out var child))
                {
                    child = new Node();
                    node.Children[segment] = child;
                }
                node = child;
            }
        }

        node.Methods ??= new Dictionary<string, T>(StringComparer.Ordinal);
        if (!node.Methods.TryAdd(method, value))
        {
            throw new InvalidOperationException(
                $"Duplicate route registration for method '{method}' and pattern '{pattern}'.");
        }
    }

    public bool TryMatch(
        string path,
        string method,
        out T value,
        out Dictionary<string, string> routeValues)
    {
        var span = path.AsSpan();
        if (span.Length == 0 || (span.Length == 1 && span[0] == '/'))
        {
            return TryRootMatch(method, out value, out routeValues);
        }

        var start = span[0] == '/' ? 1 : 0;
        var node = _root;

        List<string>? paramNames = null;
        List<string>? paramValues = null;

        for (var i = start; i <= span.Length; i++)
        {
            if (i < span.Length && span[i] != '/')
            {
                continue;
            }

            var segment = span[start..i];

            if (node.Children != null &&
                node.Children.TryGetValue(segment.ToString(), out var child))
            {
                node = child;
            }
            else if (node.ParamChild != null)
            {
                paramNames ??= new List<string>();
                paramValues ??= new List<string>();
                paramNames.Add(node.ParamName!);
                paramValues.Add(Uri.UnescapeDataString(segment.ToString()));
                node = node.ParamChild;
            }
            else
            {
                value = default!;
                routeValues = null!;
                return false;
            }

            start = i + 1;
        }

        if (node.Methods != null && node.Methods.TryGetValue(method, out value!))
        {
            routeValues = BuildRouteValues(paramNames, paramValues);
            return true;
        }

        value = default!;
        routeValues = null!;
        return false;
    }

    public IEnumerable<string> GetMethods(string path)
    {
        var node = _root;
        var segments = SplitPath(path);

        foreach (var segment in segments)
        {
            if (node.Children != null &&
                node.Children.TryGetValue(segment, out var child))
            {
                node = child;
            }
            else
            {
                return [];
            }
        }

        return node.Methods?.Keys ?? Enumerable.Empty<string>();
    }

    /// <summary>
    /// Walks the tree matching the given path against both exact and parameter segments,
    /// and returns all methods registered at the matched node.
    /// Returns <c>null</c> if the path does not match any registered pattern.
    /// </summary>
    public IEnumerable<string>? TryGetMethodsForPath(string path)
    {
        var span = path.AsSpan();
        if (span.Length == 0 || (span.Length == 1 && span[0] == '/'))
        {
            return _root.Methods?.Keys;
        }

        var start = span[0] == '/' ? 1 : 0;
        var node = _root;

        for (var i = start; i <= span.Length; i++)
        {
            if (i < span.Length && span[i] != '/')
                continue;

            var segment = span[start..i];

            if (node.Children != null &&
                node.Children.TryGetValue(segment.ToString(), out var child))
            {
                node = child;
            }
            else if (node.ParamChild != null)
            {
                node = node.ParamChild;
            }
            else
            {
                return null;
            }

            start = i + 1;
        }

        return node.Methods?.Keys;
    }

    private bool TryRootMatch(
        string method,
        out T value,
        out Dictionary<string, string> routeValues)
    {
        if (_root.Methods != null && _root.Methods.TryGetValue(method, out value!))
        {
            routeValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return true;
        }

        value = default!;
        routeValues = null!;
        return false;
    }

    private static Dictionary<string, string> BuildRouteValues(
        List<string>? paramNames,
        List<string>? paramValues)
    {
        if (paramNames is null || paramNames.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(paramNames.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < paramNames.Count; i++)
        {
            result[paramNames[i]] = paramValues![i];
        }
        return result;
    }

    private static bool IsParameter(ReadOnlySpan<char> segment, out string paramName)
    {
        if (segment.Length > 2 && segment[0] == '{' && segment[^1] == '}')
        {
            paramName = segment[1..^1].ToString();
            return true;
        }

        paramName = string.Empty;
        return false;
    }

    private static string[] SplitPath(string path)
    {
        if (path == "/")
        {
            return [];
        }

        return path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private sealed class Node
    {
        public Dictionary<string, Node>? Children;
        public Node? ParamChild;
        public string? ParamName;
        public Dictionary<string, T>? Methods;
    }
}
