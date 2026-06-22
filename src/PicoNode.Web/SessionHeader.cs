namespace PicoNode.Web;

public static class SessionHeader
{
    public static (SessionIdExtractor Extract, SessionIdSetter Set)
        Create(string headerName = "X-Session-Id")
    {
        return (
            Extract: request =>
            {
                request.Headers.TryGetValue(headerName, out var value);
                return value;
            },
            Set: (response, sessionId) =>
            {
                response.Headers.Add(headerName, sessionId);
            }
        );
    }
}
