namespace PicoHtmx;

public static class Layout
{
    public static string Page(
        string title,
        string body,
        string? styles = null,
        string? scripts = null
    )
    {
        var styleLink = styles is null ? "" : H.Link(styles);
        var scriptTag = scripts is null ? "" : H.Script(scripts);
        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <title>{H.E(title)}</title>
                {styleLink}
                <script src="https://unpkg.com/htmx.org@2"></script>
            </head>
            <body>
                {body}
                {scriptTag}
            </body>
            </html>
            """;
    }

    public static string AppShell(string sidebar, string main)
    {
        return $"""
            <div id="app" class="app-shell" style="display:flex;height:100vh">
                <div id="sidebar" class="sidebar" style="width:260px;border-right:1px solid #ddd">
                    {sidebar}
                </div>
                <div id="main" class="main" style="flex:1">
                    {main}
                </div>
            </div>
            """;
    }

    public static string NavItem(string label, string href, bool active = false)
    {
        // Keep the attrs statically typed (anonymous type via var) so the
        // generic H.Tag<T> overload is selected — object-typed attrs would
        // fail loudly (AOT preservation guard) or silently render nothing.
        if (active)
        {
            var attrs = new { href, @class = "active" };
            return H.Tag("a", H.E(label), attrs);
        }
        else
        {
            var attrs = new { href };
            return H.Tag("a", H.E(label), attrs);
        }
    }
}
