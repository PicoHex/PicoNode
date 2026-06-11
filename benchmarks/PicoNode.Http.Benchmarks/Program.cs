using PicoBench.Formatters;

namespace PicoNode.Http.Benchmarks;

public static class Program
{
    public static int Main(string[] args)
    {
        var config = ParseConfig(args);
        var formatter = new ConsoleFormatter();

        var suites = new[]
        {
            BenchmarkRunner.Run<HttpConnectionHandlerBenchmarks>(config),
            BenchmarkRunner.Run<HttpRouterBenchmarks>(config),
            BenchmarkRunner.Run<HttpPipelineBenchmarks>(config),
            BenchmarkRunner.Run<HttpTcpNodeRoundTripBenchmarks>(config),
            BenchmarkRunner.Run<HttpPipelineGetComparisonBenchmarks>(config),
            BenchmarkRunner.Run<HttpPipelinePostEchoComparisonBenchmarks>(config),
            BenchmarkRunner.Run<HttpTcpNodeRoundTripGetComparisonBenchmarks>(config),
            BenchmarkRunner.Run<HttpTcpNodeRoundTripPostEchoComparisonBenchmarks>(config),
        };

        foreach (var suite in suites)
        {
            Console.WriteLine(formatter.Format(suite));
            Console.WriteLine();
        }

        return 0;
    }

    private static BenchmarkConfig ParseConfig(string[] args) =>
        args.Length == 0
            ? BenchmarkConfig.Default
            : args[0].ToLowerInvariant() switch
            {
                "quick" => BenchmarkConfig.Quick,
                "default" => BenchmarkConfig.Default,
                "precise" => BenchmarkConfig.Precise,
                _ => throw new ArgumentException(
                    "Supported benchmark modes are: quick, default, precise.",
                    nameof(args)
                ),
            };
}
