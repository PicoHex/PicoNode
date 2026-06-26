namespace PicoNode.Agent;

/// <summary>
/// Handles the /thinking command parsing and Model.ThinkingEnabled state transitions.
/// Extracted from Program.cs for testability.
/// </summary>
public static class ThinkingCommand
{
    /// <summary>
    /// Applies a /thinking command argument to the model's reasoning state.
    /// Returns null on success, or an error message string on invalid arg.
    /// </summary>
    public static string? Apply(Model model, string arg)
    {
        if (arg is null)
        {
            return "Usage: /thinking [on|off]";
        }

        if (string.IsNullOrWhiteSpace(arg))
        {
            // Bare /thinking — toggle
            model.ThinkingEnabled = !model.ThinkingEnabled;
            return null;
        }

        if (arg is "on" or "true")
        {
            // /thinking on  or  /thinking true — always set true
            model.ThinkingEnabled = true;
            return null;
        }

        if (arg is "off" or "false")
        {
            // /thinking off  or  /thinking false — always set false
            model.ThinkingEnabled = false;
            return null;
        }

        return "Usage: /thinking [on|off]";
    }
}
