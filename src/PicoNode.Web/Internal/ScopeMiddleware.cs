namespace PicoNode.Web.Internal;

internal static class ScopeMiddleware
{
    public static WebMiddleware Create(PicoNode.Abs.IServiceProvider provider) =>
        (context, next, ct) =>
        {
            var scope = provider.CreateScope();
            context.Services = scope;
            try
            {
                return next(context, ct);
            }
            finally
            {
                scope.Dispose();
            }
        };
}
