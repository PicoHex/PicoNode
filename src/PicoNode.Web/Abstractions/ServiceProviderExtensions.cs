namespace PicoNode.Web.Abstractions;

public static class ServiceProviderExtensions
{
    public static T? GetService<T>(this IServiceScope scope) => (T?)scope.GetService(typeof(T));
}
