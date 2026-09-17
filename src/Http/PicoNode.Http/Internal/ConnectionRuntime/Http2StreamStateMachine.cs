namespace PicoNode.Http.Internal.ConnectionRuntime;

/// <summary>
/// HTTP/2 stream state machine per RFC 7540 §5.1.
/// Validates frame transitions based on current stream state.
/// </summary>
internal sealed class Http2StreamStateMachine
{
    // TryTransition runs on the connection's frame loop AND on background stream
    // tasks (CompleteResponse from the response pump / async handler), so the
    // state is guarded rather than relying on single-threaded access.
    private readonly Lock _gate = new();
    private StreamState _currentState = StreamState.Idle;
    private bool _closedByPeerRst;

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

    public StreamState CurrentState
    {
        get
        {
            lock (_gate)
            {
                return _currentState;
            }
        }
    }

    /// <summary>True when the stream was closed by the peer's RST_STREAM.</summary>
    public bool ClosedByPeerRst
    {
        get
        {
            lock (_gate)
            {
                return _closedByPeerRst;
            }
        }
    }

    public Http2StreamStateMachine(int streamId)
    {
        // streamId is accepted for constructor compatibility; the id itself
        // lives on Http2StreamState (the machine only tracks transitions).
        _ = streamId;
    }

    /// <summary>
    /// Attempts a state transition. Returns false if the trigger is invalid
    /// for the current state (protocol error).
    /// </summary>
    public bool TryTransition(Trigger trigger, out StreamState previousState)
    {
        lock (_gate)
        {
            return TryTransitionLocked(trigger, out previousState);
        }
    }

    private bool TryTransitionLocked(Trigger trigger, out StreamState previousState)
    {
        previousState = _currentState;

        if (trigger == Trigger.RstStream)
        {
            // RST_STREAM is valid from any state and is idempotent.
            if (_currentState != StreamState.Closed)
            {
                _currentState = StreamState.Closed;
            }

            _closedByPeerRst = true;
            return true;
        }

        // Read-only checks are faster than the switch below
        if (_currentState == StreamState.Closed)
            return false;

        switch (_currentState)
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
                return trigger == Trigger.Data
                    || (trigger == Trigger.EndStream && TransitionTo(StreamState.Closed));

            case StreamState.HalfClosedRemote:
                // Peer sent END_STREAM — the peer may only send
                // WINDOW_UPDATE/PRIORITY/RST_STREAM now (handled outside the
                // machine). The only legal transition left is OUR response
                // END_STREAM → Closed.
                return trigger == Trigger.EndStream && TransitionTo(StreamState.Closed);

            default:
                return false;
        }
    }

    private bool TransitionTo(StreamState newState)
    {
        _currentState = newState;
        return true;
    }
}
