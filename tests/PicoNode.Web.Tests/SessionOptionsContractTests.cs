namespace PicoNode.Web.Tests;

public sealed class SessionOptionsContractTests
{
    [Test]
    public async Task Defaults_are_20_minutes_idle_timeout()
    {
        var options = new SessionOptions();

        await Assert.That(options.IdleTimeout).IsEqualTo(TimeSpan.FromMinutes(20));
    }

    [Test]
    public async Task Defaults_are_5_minutes_cleanup_interval()
    {
        var options = new SessionOptions();

        await Assert.That(options.CleanupInterval).IsEqualTo(TimeSpan.FromMinutes(5));
    }

    [Test]
    public async Task Defaults_are_10_000_sessions()
    {
        var options = new SessionOptions();

        await Assert.That(options.MaxSessions).IsEqualTo(10_000);
    }

    [Test]
    public async Task Defaults_are_256_KiB_per_session()
    {
        var options = new SessionOptions();

        await Assert.That(options.MaxBytesPerSession).IsEqualTo(256 * 1024);
    }

    [Test]
    public async Task Defaults_are_not_equal()
    {
        var options = new SessionOptions();

        await Assert.That(options.IdleTimeout).IsNotEqualTo(options.CleanupInterval);
    }

    [Test]
    public async Task Can_set_custom_values_via_init()
    {
        var options = new SessionOptions
        {
            IdleTimeout = TimeSpan.FromMinutes(60),
            CleanupInterval = TimeSpan.FromMinutes(10),
        };

        await Assert.That(options.IdleTimeout).IsEqualTo(TimeSpan.FromMinutes(60));
        await Assert.That(options.CleanupInterval).IsEqualTo(TimeSpan.FromMinutes(10));
    }
}
