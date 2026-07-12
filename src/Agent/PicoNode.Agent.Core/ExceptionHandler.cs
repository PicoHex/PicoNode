namespace PicoNode.Agent.Domain;

/// <summary>
/// Central exception handling. Logs every exception and optionally
/// forwards the message to the frontend via an output action.
/// </summary>
public static class ExceptionHandler
{
    /// <summary>
    /// Handle an exception: always log, optionally forward to frontend.
    /// Returns false for cancellation (caller should not re-throw).
    /// </summary>
    public static bool Handle(
        Exception ex,
        ILogger? logger,
        string context,
        Action<string>? forward = null
    )
    {
        if (ex is OperationCanceledException)
            return false; // normal — no log needed

        logger?.Error($"{context}: {ex.Message}");
        forward?.Invoke(ex.Message);
        return true; // was a real error
    }

    /// <summary>Log-only, no ILogger available — uses fallback.</summary>
    public static void LogOnly(Exception ex, string context)
    {
        System.Diagnostics.Debug.WriteLine($"[{context}] {ex.Message}");
    }
}
