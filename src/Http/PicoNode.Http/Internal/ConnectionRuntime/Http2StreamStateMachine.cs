namespace PicoNode.Http.Internal.ConnectionRuntime;

/// <summary>
/// HTTP/2 stream state machine per RFC 7540 §5.1.
/// Validates frame transitions based on current stream state.
/// </summary>
internal sealed class Http2StreamStateMachine
{
    public enum StreamState
    {
        Idle,
        Open,
        HalfClosedLocal,
        HalfClosedRemote,
        Closed,
    }

    public enum Trigger
    {
        Headers,
        Data,
        EndStream,
        RstStream,
    }

    public StreamState CurrentState { get; private set; } = StreamState.Idle;
    public int StreamId { get; }

    public Http2StreamStateMachine(int streamId)
    {
        StreamId = streamId;
    }

    /// <summary>
    /// Attempts a state transition. Returns false if the trigger is invalid
    /// for the current state (protocol error).
    /// </summary>
    public bool TryTransition(Trigger trigger, out StreamState previousState)
    {
        previousState = CurrentState;

        // Read-only checks are faster than the switch below
        if (CurrentState == StreamState.Closed)
            return false;

        if (trigger == Trigger.RstStream)
        {
            CurrentState = StreamState.Closed;
            return true;
        }

        switch (CurrentState)
        {
            case StreamState.Idle:
                return trigger == Trigger.Headers ? TransitionTo(StreamState.Open) : false;

            case StreamState.Open:
                return trigger switch
                {
                    Trigger.Headers => true, // trailers
                    Trigger.Data => true,
                    // RFC 7540 §5.1: EndStream from remote → HalfClosedRemote
                    Trigger.EndStream => TransitionTo(StreamState.HalfClosedRemote),
                    _ => false,
                };

            case StreamState.HalfClosedLocal:
                // We (server) sent END_STREAM — waiting for peer's END_STREAM
                return trigger == Trigger.Data || trigger == Trigger.EndStream;

            case StreamState.HalfClosedRemote:
                // Peer sent END_STREAM — we can still send response
                return trigger == Trigger.Headers
                    || trigger == Trigger.Data
                    || trigger == Trigger.EndStream;

            default:
                return false;
        }
    }

    private bool TransitionTo(StreamState newState)
    {
        CurrentState = newState;
        return true;
    }
}
