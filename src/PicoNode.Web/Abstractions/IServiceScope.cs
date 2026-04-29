namespace PicoNode.Abs;

public interface IServiceScope : IDisposable
{
    object? GetService(Type serviceType);
}
