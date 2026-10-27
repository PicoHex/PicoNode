using System.Collections.Immutable;

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

    // ─────────────────────────── PWR001 (task T5.7, audit F-55) ───────────────────────────

    /// <summary>
    /// PWR001 was <c>Info</c> with <c>location: null</c>: an IDE cannot navigate to it and a build
    /// log does not show it, so it was worth nothing to a reader. It is a <c>Warning</c> reported
    /// at the call site now — the location <em>is</em> the report.
    /// </summary>
    [Test]
    public async Task PWR001_is_a_warning_at_the_call_site()
    {
        var source = """
            using PicoNode.Http;
            public class TestClass
            {
                public void Run()
                {
                    var route = Route<System.Func<int>>.MapGet("/a", () => 42);
                }
            }
            """;

        var diagnostics = DiagnosticsOf(source, "Test.cs");
        var pwr = diagnostics.Where(static d => d.Id == "PWR001").ToArray();

        await Assert.That(pwr.Length).IsEqualTo(1);
        await Assert.That(pwr[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
        // `location: null` is what it used to report; this is the assertion that it is gone.
        await Assert.That(pwr[0].Location.Kind).IsNotEqualTo(LocationKind.None);
        await Assert.That(pwr[0].Location.GetLineSpan().Path).IsEqualTo("Test.cs");
        // The location is the registration itself — not the file, not the lambda.
        await Assert
            .That(GeneratorHarness.TextAt(pwr[0], source))
            .IsEqualTo("Route<System.Func<int>>.MapGet(\"/a\", () => 42)");
        await Assert.That(pwr[0].GetMessage()).Contains("'int'");
    }

    /// <summary>
    /// The audit's false positive (S4): the candidate test matched the <em>name</em>
    /// <c>MapGet</c> only, so anybody's own method with that name was reported as a route
    /// registration. The call is validated through the semantic model now — a registration is a
    /// call on <c>PicoNode.Web.WebApp</c> or <c>PicoNode.Http.Route&lt;T&gt;</c>.
    /// </summary>
    [Test]
    public async Task PWR001_ignores_a_MapGet_that_is_not_a_route_registration()
    {
        var source = """
            public class Unrelated
            {
                public void MapGet(string path, System.Func<int> handler) { }
            }
            public class TestClass
            {
                public void Run(Unrelated unrelated) => unrelated.MapGet("/a", () => 42);
            }
            """;

        var diagnostics = DiagnosticsOf(source, "Test.cs");

        await Assert
            .That(string.Join(" | ", diagnostics.Select(static d => $"{d.Id} {d.GetMessage()}")))
            .IsEmpty();
    }

    /// <summary>
    /// Two shapes the old candidate list and the old return-type reader could not see at all:
    /// <c>MapPatch</c> was missing from the verb list, and a <em>block-bodied</em> handler was
    /// never inspected (the type was read from the body expression of an expression-bodied lambda
    /// only). A route registration written either way was silently unchecked.
    /// <para>
    /// Both are asserted from one run, and each is reported at its own call site: a diagnostic
    /// per registration is the point of giving it a location.
    /// </para>
    /// </summary>
    [Test]
    public async Task PWR001_covers_MapPatch_and_block_bodied_handlers()
    {
        var source = """
            using PicoNode.Http;
            public class TestClass
            {
                public void Run()
                {
                    var patch = Route<System.Func<int>>.MapPatch("/a", () => 42);
                    var block = Route<System.Func<long>>.MapGet("/b", () => { return 43L; });
                }
            }
            """;

        var diagnostics = DiagnosticsOf(source, "Test.cs");
        var pwr = diagnostics.Where(static d => d.Id == "PWR001").ToArray();

        // One diagnostic per *type* — a repeat of the same type is one thing to fix — but the two
        // shapes below return different types, so both registrations are inspected.
        await Assert
            .That(string.Join(" | ", pwr.Select(static d => d.GetMessage())))
            .IsEqualTo(
                "Route handler returns 'int'. Ensure it is serializable if used as JSON response."
                    + " | "
                    + "Route handler returns 'long'. Ensure it is serializable if used as JSON response."
            );
        await Assert
            .That(string.Join(" | ", pwr.Select(d => GeneratorHarness.TextAt(d, source))))
            .Contains("MapPatch(\"/a\", () => 42)");
        await Assert
            .That(string.Join(" | ", pwr.Select(d => GeneratorHarness.TextAt(d, source))))
            .Contains("MapGet(\"/b\", () => { return 43L; })");
    }

    /// <summary>
    /// Runs <see cref="MapMethodGenerator"/> against the shared reference closure and returns what
    /// it <em>reported</em> (a generator's diagnostics never appear in the compilation's own).
    /// </summary>
    private static ImmutableArray<Diagnostic> DiagnosticsOf(string source, string fileName)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
            path: fileName
        );

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [syntaxTree],
            GeneratorHarness.BaseReferences,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)
        );

        var runResult = CSharpGeneratorDriver
            .Create(new MapMethodGenerator())
            .RunGenerators(compilation)
            .GetRunResult();

        return runResult.Results.IsDefaultOrEmpty
            ? ImmutableArray<Diagnostic>.Empty
            : runResult.Results[0].Diagnostics;
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

        // The shared reference closure (T5.2's harness), not `AppDomain.CurrentDomain
        // .GetAssemblies()`: that list only contains what happens to be loaded, so the reference
        // set depended on test order, and reading `Assembly.Location` is IL3000 (two warnings in
        // this file until T5.7 adopted the harness closure here too).
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [syntaxTree],
            GeneratorHarness.BaseReferences,
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
