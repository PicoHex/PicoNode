namespace PicoNode.Web.Session.Abs;

public interface ISessionStore
{
    ValueTask<ISession?> LoadAsync(string sessionId, CancellationToken ct = default);

    ValueTask<ISession> CreateAsync(CancellationToken ct = default);

    ValueTask SaveAsync(string sessionId, ISession session, CancellationToken ct = default);

    ValueTask DeleteAsync(string sessionId, CancellationToken ct = default);
}
