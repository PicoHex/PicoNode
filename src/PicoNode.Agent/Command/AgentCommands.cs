namespace PicoNode.Agent;

public static class AgentCommands
{
    public static string Execute(string command, string args, Session session)
    {
        return command switch
        {
            "thinking" => ThinkingCommand.Apply(new Model(), args) ?? "thinking updated",
            "save" => $"[Session saved: {session.GetEntries().Result.Length} entries]",
            "compact" => "[Compaction triggered]",
            _ => throw new ArgumentException($"Unknown command: {command}"),
        };
    }
}
