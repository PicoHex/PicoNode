namespace PicoNode.Tests;

public sealed class SocketIoEventArgsPoolTests
{
    [Test]
    public async Task Rent_resets_socket_specific_state()
    {
        using var pool = new SocketIoEventArgsPool();
        var eventArgs = pool.Rent();

        eventArgs.AcceptSocket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        eventArgs.DisconnectReuseSocket = true;
        eventArgs.RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 12345);
        eventArgs.UserToken = new object();
        eventArgs.SetBuffer(new byte[16], 0, 16);

        pool.Return(eventArgs);

        var rentedAgain = pool.Rent();
        try
        {
            await Assert.That(ReferenceEquals(eventArgs, rentedAgain)).IsTrue();
            await Assert.That(rentedAgain.AcceptSocket).IsNull();
            await Assert.That(rentedAgain.DisconnectReuseSocket).IsFalse();
            await Assert.That(rentedAgain.RemoteEndPoint).IsNull();
            await Assert.That(rentedAgain.UserToken).IsNull();
            await Assert.That(rentedAgain.Count).IsEqualTo(0);
        }
        finally
        {
            pool.Return(rentedAgain);
        }
    }

    [Test]
    public async Task Rent_resets_buffer_and_accept_socket()
    {
        using var pool = new SocketIoEventArgsPool();
        var eventArgs = pool.Rent();

        await Assert.That(eventArgs.AcceptSocket).IsNull();
        await Assert.That(eventArgs.Count).IsEqualTo(0);

        pool.Return(eventArgs);
    }

    [Test]
    public async Task Return_after_dispose_does_not_throw()
    {
        var pool = new SocketIoEventArgsPool();
        var eventArgs = pool.Rent();
        eventArgs.SetBuffer(ArrayPool<byte>.Shared.Rent(8), 0, 8);

        pool.Dispose();
        pool.Return(eventArgs);

        await Assert.That(eventArgs.Buffer).IsNull();
    }

    [Test]
    public async Task Rent_after_dispose_throws()
    {
        using var pool = new SocketIoEventArgsPool();
        pool.Dispose();

        await Assert.That(() => pool.Rent()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task Dispose_can_be_called_multiple_times()
    {
        using var pool = new SocketIoEventArgsPool();
        pool.Dispose();
        pool.Dispose();

        await Assert.That(() => pool.Rent()).Throws<ObjectDisposedException>();
    }
}
