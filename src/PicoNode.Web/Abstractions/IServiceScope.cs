namespace PicoNode.Web.Abstractions;

public interface IServiceScope : IDisposable
{
    object? GetService(Type serviceType);
}
