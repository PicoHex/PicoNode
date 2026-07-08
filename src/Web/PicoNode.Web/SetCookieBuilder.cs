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

    public KeyValuePair<string, string> Build()
    {
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
}
