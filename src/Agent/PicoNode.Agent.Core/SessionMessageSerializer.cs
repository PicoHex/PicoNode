using System.Text;
using PicoJetson;
using PicoNode.AI;

namespace PicoNode.Agent.Domain;

public static class SessionMessageSerializer
{
    private static readonly JsonOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ToJson(Message[] messages)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < messages.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(JsonSerializer.Serialize(messages[i], _options));
        }
        sb.Append(']');
        return sb.ToString();
    }
}
