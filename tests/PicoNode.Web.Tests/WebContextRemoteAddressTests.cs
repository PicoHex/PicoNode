using System.Net;

namespace PicoNode.Web.Tests;

/// <summary>
/// The peer address plumbing (spec §3.3, P2): the transport already knows the
/// connected peer (<c>ITcpConnectionContext.RemoteEndPoint</c>); these tests pin
/// the two convenience projections on <see cref="WebContext"/>.
/// </summary>
public sealed class WebContextRemoteAddressTests
{
    [Test]
    public async Task RemoteAddress_Maps_From_The_Request_Endpoint()
    {
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 54321);
        var context = WebContext.Create(new HttpRequest { Method = "GET", Target = "/" });
        context.Request.RemoteEndPoint = endpoint;

        await Assert.That(context.RemoteEndPoint).IsSameReferenceAs(endpoint);
        await Assert.That(context.RemoteAddress).IsEqualTo(IPAddress.Parse("203.0.113.7"));
    }

    [Test]
    public async Task RemoteAddress_Is_Null_Without_A_Peer()
    {
        var context = WebContext.Create(new HttpRequest { Method = "GET", Target = "/" });

        await Assert.That(context.RemoteEndPoint).IsNull();
        await Assert.That(context.RemoteAddress).IsNull();
    }
}
