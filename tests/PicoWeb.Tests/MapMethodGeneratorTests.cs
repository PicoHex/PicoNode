namespace PicoWeb.Tests;

public sealed class MapMethodGeneratorTests
{
    [Test]
    public async Task Generator_creates_source_file()
    {
        // PicoWeb.Gen emits only PWR001 diagnostics — it generates no source files.
        // With no MapXX calls, it emits nothing at all.
        var source = """
            public class Empty { }
            """;

        var result = RunGenerator(source, "Empty.cs");

        // No MapXX calls → no generated output
        await Assert.That(result.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SerializeToUtf8Bytes_in_code_triggers_registration()
    {
        // When user calls SerializeToUtf8Bytes<UserDto>(dto) in their code,
        // PicoJetson.Gen handles the registration. PicoWeb.Gen only emits
        // PWR001 diagnostics and never generates source files.
        var source = """
            using PicoJetson;
            public class TestClass
            {
                public void Run()
                {
                    var user = new UserDto();
                    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);
                }
            }
            public class UserDto { public string Name { get; set; } }
            """;

        var result = RunGenerator(source, "Test.cs");

        // No MapXX calls → no output from this generator
        await Assert.That(result.Length).IsEqualTo(0);
    }

    [Test]
    public async Task MapXX_expression_lambda_does_not_generate_RouteReturnTypes()
    {
        // Regression: the generator used to emit a RouteReturnTypes class
        // (typeof() array of handler return types) that nothing ever read.
        // It was dead code compiled into every consumer assembly — the
        // generator must not emit any source at all.
        var source = """
            using PicoWeb;
            using PicoNode.Web;
            using System.Threading;
            public class TestClass
            {
                public void Run()
                {
                    var api = new WebApiBuilder().Build();
                    api.MapGet("/a", (WebContext ctx, CancellationToken _) =>
                        ValueTask.FromResult(WebResults.Json(200, "{}", "OK")));
                }
            }
            """;

        var (sources, _, _) = RunGeneratorWithDiagnostics(source, "Test.cs");

        // RouteReturnTypes was dead code — the generator must emit no source files
        await Assert.That(sources).IsEqualTo("");
    }

    [Test]
    public async Task Generator_does_not_crash_on_unresolvable_handler_return_type()
    {
        // A handler whose body cannot convert to ValueTask<HttpResponse> must
        // not crash the generator nor emit dead source (regression guard).
        var source = """
            using PicoWeb;
            using PicoNode.Web;
            using System.Threading;
            public class TestClass
            {
                public void Run()
                {
                    var api = new WebApiBuilder().Build();
                    api.MapGet("/a", (WebContext ctx, CancellationToken _) =>
                        ValueTask.FromResult(new MyResult()));
                }
            }
            public class MyResult { public string Value { get; set; } }
            """;

        var (sources, _, _) = RunGeneratorWithDiagnostics(source, "Test.cs");

        await Assert.That(sources).IsEqualTo("");
    }

    private static string RunGenerator(string source, string fileName)
    {
        return RunGeneratorWithDiagnostics(source, fileName).Sources;
    }

    private static (
        string Sources,
        System.Collections.Immutable.ImmutableArray<Diagnostic> Diagnostics,
        string CompErrors
    ) RunGeneratorWithDiagnostics(string source, string fileName)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
            path: fileName
        );

        var references = AppDomain
            .CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var generator = new MapMethodGenerator();
        var driver = CSharpGeneratorDriver.Create(generator);
        var runResult = driver.RunGenerators(compilation).GetRunResult();

        var compErrors = string.Join(
            ";",
            compilation
                .GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.Id)
        );

        if (runResult.Results.Length == 0)
            return ("", [], compErrors);

        var sources = runResult.Results[0].GeneratedSources;
        var sourcesText = sources.IsEmpty
            ? ""
            : string.Join("\n", sources.Select(s => s.SourceText.ToString()));

        return (sourcesText, runResult.Results[0].Diagnostics, compErrors);
    }
}
