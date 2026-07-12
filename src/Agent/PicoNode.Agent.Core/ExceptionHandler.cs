namespace PicoNode.Agent.Domain;

/// <summary>
/// Central exception handling. In production, call <see cref="Initialize"/>
/// once at startup with a real logger. Until then, uses Debug.WriteLine.
/// </summary>
public static class ExceptionHandler
{
    private static ILogger? _logger;

    /// <summary>Call once at startup to enable real logging.</summary>
    public static void Initialize(ILogger logger) => _logger = logger;

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
            return false;

        (logger ?? _logger)?.Error($"{context}: {ex.Message}");
        forward?.Invoke(ex.Message);
        return true;
    }

    /// <summary>Log using the global logger if initialized, else Debug.WriteLine.</summary>
    public static void LogOnly(Exception ex, string context)
    {
        if (_logger is not null)
            _logger.Error($"{context}: {ex.Message}");
        else
            System.Diagnostics.Debug.WriteLine($"[{context}] {ex.Message}");
    }
}
