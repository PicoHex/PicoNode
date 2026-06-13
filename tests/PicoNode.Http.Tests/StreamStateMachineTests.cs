
namespace PicoNode.Http.Tests;

public sealed class StreamStateMachineTests
{
    [Test]
    public async Task Idle_accepts_headers()
    {
        var sm = new Http2StreamStateMachine(1);
        var result = sm.TryTransition(Http2StreamStateMachine.Trigger.Headers, out _);
        await Assert.That(result).IsTrue();
        await Assert.That(sm.CurrentState).IsEqualTo(Http2StreamStateMachine.StreamState.Open);
    }

    [Test]
    public async Task Idle_rejects_data()
    {
        var sm = new Http2StreamStateMachine(1);
        var result = sm.TryTransition(Http2StreamStateMachine.Trigger.Data, out _);
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task Open_transitions_to_half_closed_on_end_stream()
    {
        var sm = new Http2StreamStateMachine(1);
        sm.TryTransition(Http2StreamStateMachine.Trigger.Headers, out _);
        sm.TryTransition(Http2StreamStateMachine.Trigger.EndStream, out _);
        await Assert
            .That(sm.CurrentState)
            .IsEqualTo(Http2StreamStateMachine.StreamState.HalfClosedLocal);
    }

    [Test]
    public async Task Closed_rejects_all_triggers()
    {
        var sm = new Http2StreamStateMachine(1);
        sm.TryTransition(Http2StreamStateMachine.Trigger.RstStream, out _);
        await Assert.That(sm.CurrentState).IsEqualTo(Http2StreamStateMachine.StreamState.Closed);

        var result = sm.TryTransition(Http2StreamStateMachine.Trigger.Headers, out _);
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task RstStream_always_transitions_to_closed()
    {
        var sm = new Http2StreamStateMachine(1);
        sm.TryTransition(Http2StreamStateMachine.Trigger.Headers, out _);
        sm.TryTransition(Http2StreamStateMachine.Trigger.RstStream, out _);
        await Assert.That(sm.CurrentState).IsEqualTo(Http2StreamStateMachine.StreamState.Closed);
    }
}
