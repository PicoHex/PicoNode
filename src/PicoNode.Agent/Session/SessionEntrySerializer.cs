using System.Text;
using System.Text.Json;

namespace PicoNode.Agent;

/// <summary>
/// Serializer for session entries using JsonDocument/Utf8JsonWriter (AOT-safe BCL types).
/// PicoJetson handles Message sub-objects; STJ handles the container entries.
/// </summary>
internal static class SessionEntrySerializer
{
    public static string Serialize(SessionTreeEntryBase entry)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        WriteEntry(writer, entry);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static SessionTreeEntryBase? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("$type", out var typeProp))
            return null;
        var type = typeProp.GetString() ?? "";
        return type switch
        {
            "message" => ReadMessageEntry(root),
            "compaction" => ReadCompactionEntry(root),
            "branch_summary" => ReadBranchSummaryEntry(root),
            "custom" => ReadCustomEntry(root),
            "custom_message" => ReadCustomMessageEntry(root),
            "label" => ReadLabelEntry(root),
            "session_info" => ReadSessionInfoEntry(root),
            "model_change" => ReadModelChangeEntry(root),
            "thinking_level_change" => ReadThinkingLevelChangeEntry(root),
            "active_tools_change" => ReadActiveToolsChangeEntry(root),
            "leaf" => ReadLeafEntry(root),
            _ => null,
        };
    }

    private static void WriteEntry(Utf8JsonWriter w, SessionTreeEntryBase e)
    {
        w.WriteStartObject();
        WriteBase(w, e);
        switch (e)
        {
            case MessageEntry me:
                WriteMessageEntry(w, me);
                break;
            case CompactionEntry ce:
                WriteCompactionEntry(w, ce);
                break;
            case BranchSummaryEntry bs:
                WriteBranchSummaryEntry(w, bs);
                break;
            case CustomEntry cu:
                WriteCustomEntry(w, cu);
                break;
            case CustomMessageEntry cm:
                WriteCustomMessageEntry(w, cm);
                break;
            case LabelEntry le:
                WriteLabelEntry(w, le);
                break;
            case SessionInfoEntry si:
                WriteSessionInfoEntry(w, si);
                break;
            case ModelChangeEntry mc:
                WriteModelChangeEntry(w, mc);
                break;
            case ThinkingLevelChangeEntry tc:
                WriteThinkingLevelChangeEntry(w, tc);
                break;
            case ActiveToolsChangeEntry ac:
                WriteActiveToolsChangeEntry(w, ac);
                break;
            case LeafEntry lf:
                WriteLeafEntry(w, lf);
                break;
        }
        w.WriteEndObject();
    }

    private static void WriteBase(Utf8JsonWriter w, SessionTreeEntryBase e)
    {
        w.WriteString("Id", e.Id);
        if (e.ParentId is not null)
            w.WriteString("ParentId", e.ParentId);
        w.WriteString("Timestamp", e.Timestamp);
    }

    private static void WriteMessageEntry(Utf8JsonWriter w, MessageEntry e)
    {
        w.WriteString("$type", "message");
        w.WritePropertyName("Message");
        w.WriteRawValue(PicoJetson.JsonSerializer.Serialize(e.Message));
    }

    private static void WriteCompactionEntry(Utf8JsonWriter w, CompactionEntry e)
    {
        w.WriteString("$type", "compaction");
        w.WriteString("Summary", e.Summary);
        w.WriteString("FirstKeptEntryId", e.FirstKeptEntryId);
        w.WriteNumber("TokensBefore", e.TokensBefore);
    }

    private static void WriteBranchSummaryEntry(Utf8JsonWriter w, BranchSummaryEntry e)
    {
        w.WriteString("$type", "branch_summary");
        w.WriteString("FromId", e.FromId);
        w.WriteString("Summary", e.Summary);
    }

    private static void WriteCustomEntry(Utf8JsonWriter w, CustomEntry e)
    {
        w.WriteString("$type", "custom");
        w.WriteString("CustomType", e.CustomType);
    }

    private static void WriteCustomMessageEntry(Utf8JsonWriter w, CustomMessageEntry e)
    {
        w.WriteString("$type", "custom_message");
        w.WriteString("CustomType", e.CustomType);
        if (e.Content is string s)
            w.WriteString("Content", s);
        w.WriteBoolean("Display", e.Display);
    }

    private static void WriteLabelEntry(Utf8JsonWriter w, LabelEntry e)
    {
        w.WriteString("$type", "label");
        w.WriteString("TargetId", e.TargetId);
        if (e.Label is not null)
            w.WriteString("Label", e.Label);
    }

    private static void WriteSessionInfoEntry(Utf8JsonWriter w, SessionInfoEntry e)
    {
        w.WriteString("$type", "session_info");
        if (e.Name is not null)
            w.WriteString("Name", e.Name);
    }

    private static void WriteModelChangeEntry(Utf8JsonWriter w, ModelChangeEntry e)
    {
        w.WriteString("$type", "model_change");
        w.WriteString("Provider", e.Provider);
        w.WriteString("ModelId", e.ModelId);
    }

    private static void WriteThinkingLevelChangeEntry(Utf8JsonWriter w, ThinkingLevelChangeEntry e)
    {
        w.WriteString("$type", "thinking_level_change");
        w.WriteString("ThinkingLevel", e.ThinkingLevel);
    }

    private static void WriteActiveToolsChangeEntry(Utf8JsonWriter w, ActiveToolsChangeEntry e)
    {
        w.WriteString("$type", "active_tools_change");
        w.WriteStartArray("ActiveToolNames");
        foreach (var t in e.ActiveToolNames)
            w.WriteStringValue(t);
        w.WriteEndArray();
    }

    private static void WriteLeafEntry(Utf8JsonWriter w, LeafEntry e)
    {
        w.WriteString("$type", "leaf");
        if (e.TargetId is not null)
            w.WriteString("TargetId", e.TargetId);
    }

    private static MessageEntry ReadMessageEntry(JsonElement root)
    {
        var b = ReadBase(root);
        Message? msg = null;
        if (root.TryGetProperty("Message", out var m))
            msg = PicoJetson.JsonSerializer.Deserialize<Message>(
                Encoding.UTF8.GetBytes(m.GetRawText())
            );
        return new()
        {
            Id = b.Id,
            ParentId = b.ParentId,
            Timestamp = b.Timestamp,
            Message = msg ?? new(),
        };
    }

    private static CompactionEntry ReadCompactionEntry(JsonElement root)
    {
        var b = ReadBase(root);
        return new()
        {
            Id = b.Id,
            ParentId = b.ParentId,
            Timestamp = b.Timestamp,
            Summary = S(root, "Summary"),
            FirstKeptEntryId = S(root, "FirstKeptEntryId"),
            TokensBefore = L(root, "TokensBefore"),
        };
    }

    private static BranchSummaryEntry ReadBranchSummaryEntry(JsonElement root)
    {
        var b = ReadBase(root);
        return new()
        {
            Id = b.Id,
            ParentId = b.ParentId,
            Timestamp = b.Timestamp,
            FromId = S(root, "FromId"),
            Summary = S(root, "Summary"),
        };
    }

    private static CustomEntry ReadCustomEntry(JsonElement root)
    {
        var b = ReadBase(root);
        return new()
        {
            Id = b.Id,
            ParentId = b.ParentId,
            Timestamp = b.Timestamp,
            CustomType = S(root, "CustomType"),
        };
    }

    private static CustomMessageEntry ReadCustomMessageEntry(JsonElement root)
    {
        var b = ReadBase(root);
        return new()
        {
            Id = b.Id,
            ParentId = b.ParentId,
            Timestamp = b.Timestamp,
            CustomType = S(root, "CustomType"),
            Content = S(root, "Content"),
            Display = !root.TryGetProperty("Display", out var d) || d.GetBoolean(),
        };
    }

    private static LabelEntry ReadLabelEntry(JsonElement root)
    {
        var b = ReadBase(root);
        return new()
        {
            Id = b.Id,
            ParentId = b.ParentId,
            Timestamp = b.Timestamp,
            TargetId = S(root, "TargetId"),
            Label = root.TryGetProperty("Label", out var l) ? l.GetString() : null,
        };
    }

    private static SessionInfoEntry ReadSessionInfoEntry(JsonElement root)
    {
        var b = ReadBase(root);
        return new()
        {
            Id = b.Id,
            ParentId = b.ParentId,
            Timestamp = b.Timestamp,
            Name = root.TryGetProperty("Name", out var n) ? n.GetString() : null,
        };
    }

    private static ModelChangeEntry ReadModelChangeEntry(JsonElement root)
    {
        var b = ReadBase(root);
        return new()
        {
            Id = b.Id,
            ParentId = b.ParentId,
            Timestamp = b.Timestamp,
            Provider = S(root, "Provider"),
            ModelId = S(root, "ModelId"),
        };
    }

    private static ThinkingLevelChangeEntry ReadThinkingLevelChangeEntry(JsonElement root)
    {
        var b = ReadBase(root);
        return new()
        {
            Id = b.Id,
            ParentId = b.ParentId,
            Timestamp = b.Timestamp,
            ThinkingLevel = S(root, "ThinkingLevel"),
        };
    }

    private static ActiveToolsChangeEntry ReadActiveToolsChangeEntry(JsonElement root)
    {
        var b = ReadBase(root);
        var tools = new List<string>();
        if (
            root.TryGetProperty("ActiveToolNames", out var arr)
            && arr.ValueKind == JsonValueKind.Array
        )
            foreach (var t in arr.EnumerateArray())
                tools.Add(t.GetString() ?? "");
        return new()
        {
            Id = b.Id,
            ParentId = b.ParentId,
            Timestamp = b.Timestamp,
            ActiveToolNames = tools.ToArray(),
        };
    }

    private static LeafEntry ReadLeafEntry(JsonElement root)
    {
        var b = ReadBase(root);
        return new()
        {
            Id = b.Id,
            ParentId = b.ParentId,
            Timestamp = b.Timestamp,
            TargetId = root.TryGetProperty("TargetId", out var ti) ? ti.GetString() : null,
        };
    }

    private static (string Id, string? ParentId, string Timestamp) ReadBase(JsonElement root) =>
        (
            S(root, "Id"),
            root.TryGetProperty("ParentId", out var p) ? p.GetString() : null,
            S(root, "Timestamp")
        );

    private static string S(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    private static long L(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.TryGetInt64(out var l) ? l : 0;
}
