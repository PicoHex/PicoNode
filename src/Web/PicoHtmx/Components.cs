namespace PicoHtmx;

public static class Ux
{
    public static string ListPage<T>(string title, IEnumerable<T> items,
        Func<T, string> renderItem, string emptyMsg = "No items")
    {
        var list = items.Any()
            ? string.Join("", items.Select(renderItem))
            : H.Div(H.E(emptyMsg), new { @class = "empty" });
        return $"""
        <div class="list-page">
            <div class="list-header">{H.E(title)}</div>
            <div class="list-items">{list}</div>
        </div>
        """;
    }

    public static string Form(string action, string method = "post",
        string? content = null, string? error = null)
    {
        var errorHtml = error is null ? "" : H.Div(H.E(error), new { @class = "form-error" });
        return $"""
        <form action="{H.E(action)}" method="{H.E(method)}" hx-target="this" hx-swap="outerHTML">
            {errorHtml}
            {content}
        </form>
        """;
    }

    public static string FormField(string label, string name,
        string? value = null, string? type = "text", bool required = false)
    {
        var reqAttr = required ? " required" : "";
        return $"""
        <div class="form-group">
            <label for="{H.E(name)}">{H.E(label)}</label>
            <input type="{H.E(type)}" id="{H.E(name)}" name="{H.E(name)}"
                   value="{H.E(value ?? "")}"{reqAttr} />
        </div>
        """;
    }

    public static string ConfirmDialog(string message, string confirmUrl)
    {
        return $"""
        <div class="confirm-dialog">
            <p>{H.E(message)}</p>
            <button class="btn-danger" hx-post="{H.E(confirmUrl)}"
                    hx-target="closest .confirm-dialog" hx-swap="outerHTML">Confirm</button>
            <button class="btn-secondary" onclick="this.closest('.confirm-dialog').remove()">Cancel</button>
        </div>
        """;
    }

    public static string Toast(string message, bool isError = false)
    {
        var cls = isError ? "toast error" : "toast success";
        return H.Div(H.E(message), new { @class = cls });
    }

    public static string Spinner()
    {
        return H.Div("", new { @class = "spinner" });
    }
}
