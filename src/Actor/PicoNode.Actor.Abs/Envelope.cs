namespace PicoNode.Actor.Abs;

/// <summary>
/// Internal envelope that wraps a command with an optional TaskCompletionSource for Ask semantics.
/// Users never touch this type.
/// </summary>
internal sealed class Envelope
{
    public ICommand Command { get; init; } = null!;
    public TaskCompletionSource<object?>? Tcs { get; init; }
}
