namespace PicoNode.Agent;

public static class ProtocolConstants
{
    // ContentBlock types
    public const string BlockTypeText = "text";
    public const string BlockTypeToolCall = "tool_call";

    // Message roles
    public const string RoleUser = "user";
    public const string RoleAssistant = "assistant";
    public const string RoleToolResult = "toolResult";

    // Capability protocol
    public const string KindToolCall = "tool_call";
    public const string KindHook = "hook";
    public const string KindDescribe = "describe";
    public const string KindPing = "ping";
    public const string HookEventToolCall = "on_tool_call";
    public const string ActionBlock = "block";
    public const string ActionAllow = "allow";
    public const string ActionModify = "modify";
    public const string FieldContent = "content";
    public const string FieldIsError = "isError";
    public const string FieldAction = "action";
    public const string FieldArgs = "args";
    public const string FieldToolCallId = "toolCallId";
    public const string FieldToolName = "toolName";

    // Stop reasons
    public const string StopReasonEndTurn = "end_turn";
    public const string StopReasonToolUse = "tool_use";
    public const string StopReasonError = "error";
}

public static class FileSystemConstants
{
    public const string CapabilitiesDir = "capabilities";
    public const string KnowledgeDir = "knowledge";
    public const string ManifestFile = "manifest.json";
    public const string SkillFile = "SKILL.md";
    public const string SessionsDir = "sessions";
    public const string AgentHomeDir = ".pico-agent";
    public const string PackagesDir = "packages";
    public const string SkillsDir = "skills";
    public const string ProjectSkillsDir = ".pico/skills";
}

public static class YmlConstants
{
    public const string FrontmatterDelim = "---";
    public const string KeyName = "name";
    public const string KeyDescription = "description";
}
