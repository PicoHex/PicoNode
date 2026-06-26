namespace PicoNode.Agent;

/// <summary>
/// Handles the /thinking command parsing and Model.ThinkingEnabled state transitions.
/// Extracted from Program.cs for testability.
/// </summary>
public static class ThinkingCommand
{
    /// <summary>
    /// Applies a /thinking command argument to the model's thinking state.
    /// Returns null on success, or an error message string on invalid arg.
    /// </summary>
    public static string? Apply(Model model, string arg)
    {
        if (arg is null)
        {
            return "Usage: /thinking [on|off|minimal|low|medium|high|xhigh]";
        }

        if (string.IsNullOrWhiteSpace(arg))
        {
            // Bare /thinking — toggle
            model.ThinkingEnabled = !model.ThinkingEnabled;
            return null;
        }

        if (arg is "on" or "true")
        {
            // /thinking on — enable with current level
            model.ThinkingEnabled = true;
            return null;
        }

        if (arg is "off" or "false")
        {
            // /thinking off — disable, preserve level
            model.ThinkingEnabled = false;
            return null;
        }

        // Try to parse as a ThinkingLevel
        var parsed = AgentConfig.ParseLevel(arg);
        if (parsed is { } level)
        {
            model.ThinkingEnabled = true;
            model.ThinkingLevel = level;
            return null;
        }

        return "Usage: /thinking [on|off|minimal|low|medium|high|xhigh]";
    }
}
