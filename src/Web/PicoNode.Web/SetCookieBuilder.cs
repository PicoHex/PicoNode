namespace PicoNode.Web;

public sealed class SetCookieBuilder
{
    private readonly string _name;
    private readonly string _value;
    private string? _domain;
    private string? _path;
    private DateTimeOffset? _expires;
    private int? _maxAge;
    private bool _secure;
    private bool _httpOnly;
    private string? _sameSite;

    public SetCookieBuilder(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _name = name;
        _value = value;
    }

    public SetCookieBuilder Domain(string domain)
    {
        _domain = domain;
        return this;
    }

    public SetCookieBuilder Path(string path)
    {
        _path = path;
        return this;
    }

    public SetCookieBuilder Expires(DateTimeOffset expires)
    {
        _expires = expires;
        return this;
    }

    public SetCookieBuilder MaxAge(int seconds)
    {
        _maxAge = seconds;
        return this;
    }

    public SetCookieBuilder Secure()
    {
        _secure = true;
        return this;
    }

    public SetCookieBuilder HttpOnly()
    {
        _httpOnly = true;
        return this;
    }

    public SetCookieBuilder SameSite(string value)
    {
        _sameSite = value;
        return this;
    }

    /// <summary>
    /// Builds the <c>Set-Cookie</c> header pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value is validated against the RFC 6265 <c>cookie-octet</c> production
    /// (<c>%x21 / %x23-2B / %x2D-3A / %x3C-5B / %x5D-7E</c>) and rejected when it fails.
    /// Concatenating it verbatim used to let <c>;</c> through — it is not a CR/LF, so the
    /// serializer's line-break filter never saw it — which appended arbitrary attributes to
    /// the cookie (<c>new SetCookieBuilder("theme", "x; Path=/admin")</c> wrote a cookie for
    /// <c>/admin</c> from a value the request controlled).
    /// </para>
    /// <para>
    /// <b>Reject, not encode.</b> Percent-encoding would silently change the value the
    /// caller stores and hands back (a client would echo <c>%3B</c>, not <c>;</c>), and a
    /// value that needs encoding is almost always an attack or a design error, not data.
    /// The empty value stays legal (<c>cookie-value = *cookie-octet</c>) so cookie deletion
    /// via <c>Max-Age=0</c> keeps working.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The name is not an RFC 2616 token, the value is not a run of <c>cookie-octet</c>,
    /// <c>SameSite</c> is not
    /// <c>Strict</c>/<c>Lax</c>/<c>None</c>, or <c>Domain</c>/<c>Path</c> contains <c>;</c> or <c>,</c>.
    /// </exception>
    public KeyValuePair<string, string> Build()
    {
        ValidateName(_name);
        ValidateValue(_value);
        ValidateAttribute(_domain, "Domain", "domain");
        ValidateAttribute(_path, "Path", "path");
        ValidateSameSite(_sameSite);

        var sb = new StringBuilder();
        sb.Append(_name).Append('=').Append(_value);

        if (_domain is not null)
        {
            sb.Append("; Domain=").Append(_domain);
        }

        if (_path is not null)
        {
            sb.Append("; Path=").Append(_path);
        }

        if (_expires is { } exp)
        {
            sb.Append("; Expires=")
                .Append(exp.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
        }

        if (_maxAge is { } age)
        {
            sb.Append("; Max-Age=").Append(age);
        }

        if (_secure)
        {
            sb.Append("; Secure");
        }

        if (_httpOnly)
        {
            sb.Append("; HttpOnly");
        }

        if (_sameSite is not null)
        {
            sb.Append("; SameSite=").Append(_sameSite);
        }

        return new KeyValuePair<string, string>(HttpHeaderNames.SetCookie, sb.ToString());
    }

    /// <summary>RFC 2616 <c>token</c>: the only legal shape for the name half of <c>name=value</c>.</summary>
    private static void ValidateName(string name)
    {
        for (var i = 0; i < name.Length; i++)
        {
            if (IsTokenChar(name[i]))
                continue;

            throw new ArgumentException(
                $"The cookie name contains 0x{(int)name[i]:X2} at index {i}, which is not an "
                    + "RFC 2616 token character. The name is written verbatim into "
                    + "`name=value`, so a separator such as ';' would inject an attribute or "
                    + "split the Set-Cookie line.",
                nameof(name)
            );
        }
    }

    /// <summary>RFC 6265 <c>cookie-value</c>: a (possibly empty) run of <c>cookie-octet</c>.</summary>
    private static void ValidateValue(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (IsCookieOctet(value[i]))
                continue;

            throw new ArgumentException(
                $"The cookie value contains 0x{(int)value[i]:X2} at index {i}, which is not an "
                    + "RFC 6265 cookie-octet (allowed: %x21 / %x23-2B / %x2D-3A / %x3C-5B / "
                    + "%x5D-7E). The value is rejected rather than percent-encoded: encoding "
                    + "would store a different value than the caller asked for. Separators, "
                    + "quotes, spaces and non-ASCII must be encoded by the caller.",
                nameof(value)
            );
        }
    }

    /// <summary><c>Domain</c> and <c>Path</c> are written verbatim; a separator would inject an attribute.</summary>
    private static void ValidateAttribute(string? value, string attribute, string parameterName)
    {
        if (value is null)
            return;

        if (value.AsSpan().IndexOfAny(';', ',') < 0)
            return;

        throw new ArgumentException(
            $"The cookie {attribute} must not contain ';' or ',': both are Set-Cookie "
                + "separators, so either would inject an extra attribute or split the header.",
            parameterName
        );
    }

    private static void ValidateSameSite(string? value)
    {
        if (value is null)
            return;

        if (
            value.Equals("Strict", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Lax", StringComparison.OrdinalIgnoreCase)
            || value.Equals("None", StringComparison.OrdinalIgnoreCase)
        )
            return;

        throw new ArgumentException(
            $"SameSite must be Strict, Lax or None (case-insensitive), got '{value}'.",
            nameof(value)
        );
    }

    private static bool IsCookieOctet(char c) =>
        c == '\u0021'
        || c is >= '\u0023' and <= '\u002B'
        || c is >= '\u002D' and <= '\u003A'
        || c is >= '\u003C' and <= '\u005B'
        || c is >= '\u005D' and <= '\u007E';

    /// <summary>RFC 2616 <c>token</c> symbols; quote/backtick variants stay literal.</summary>
    private static bool IsTokenChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || TokenSymbols.Contains(c);

    private const string TokenSymbols = "!#$%&'*+-.^_`|~";
}
