namespace PicoNode.Web.Internal;

internal static class ScopeMiddleware
{
    public static WebMiddleware Create(ISvcContainer provider) =>
        async (context, next, ct) =>
        {
            var scope = provider.CreateScope();
            context.Services = scope;
            try
            {
                return await next(context, ct);
            }
            finally
            {
                await scope.DisposeAsync();
            }
        };
}
