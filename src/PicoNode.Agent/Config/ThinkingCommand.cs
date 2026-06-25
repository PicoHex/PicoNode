namespace PicoNode.Agent;

/// <summary>
/// Handles the /thinking command parsing and Model.Reasoning state transitions.
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
        if (string.IsNullOrWhiteSpace(arg))
        {
            // Bare /thinking — toggle
            model.Reasoning = !model.Reasoning;
            return null;
        }

        if (arg is "on" or "true")
        {
            // /thinking on  or  /thinking true — always set true
            model.Reasoning = true;
            return null;
        }

        if (arg is "off" or "false")
        {
            // /thinking off  or  /thinking false — always set false
            model.Reasoning = false;
            return null;
        }

        return "Usage: /thinking [on|off]";
    }
}
