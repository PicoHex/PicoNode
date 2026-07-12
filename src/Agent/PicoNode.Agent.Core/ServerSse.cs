using System.Text;

namespace PicoNode.Agent.Domain;

/// <summary>
/// Builds SSE frames with protocol-native event: field for multiplexing.
/// </summary>
public static class ServerSse
{
    public static string BuildFrame(ActorOutputEvent evt)
    {
        var json = BuildJson(evt);
        if (evt.TurnId is { Length: > 0 })
            return $"event: {evt.TurnId}\ndata: {json}\n\n";
        return $"data: {json}\n\n";
    }

    private static string BuildJson(ActorOutputEvent evt)
    {
        var tp = evt.Type == "text" ? "delta" : evt.Type;
        if (tp == "done")
            return "{\"type\":\"done\"}";

        var sb = new StringBuilder(128);
        sb.Append("{\"type\":\"");
        sb.Append(tp);
        sb.Append('"');

        sb.Append(",\"content\":\"");
        sb.Append(EscapeJsonString(evt.Data ?? string.Empty));
        sb.Append('"');

        if (evt.ToolCallId is { Length: > 0 })
        {
            sb.Append(",\"toolCallId\":\"");
            sb.Append(evt.ToolCallId);
            sb.Append('"');
        }

        if (evt.ToolName is { Length: > 0 })
        {
            sb.Append(",\"toolName\":\"");
            sb.Append(evt.ToolName);
            sb.Append('"');
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static string EscapeJsonString(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
