namespace PicoNode.Web.Abstractions;

public interface IServiceProvider
{
    IServiceScope CreateScope();
}
