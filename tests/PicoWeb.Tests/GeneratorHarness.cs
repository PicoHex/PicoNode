using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PicoWeb.Tests;

/// <summary>
/// The shared spine of the Controllers.Gen suites: one reference set, one generator
/// driver, and — the part a string assertion cannot do — a real
/// <see cref="Compilation.Emit(Stream)"/> of the tree the generator emitted.
/// <para>
/// Why this exists (audit F-51…F-53): a source generator's worst symptom is not a
/// missing line, it is emitted code that does not compile (CS1503 / CS0716 / CS0246 /
/// CS0101). <c>Assert.That(generated).Contains(…)</c> is blind to all four, so every
/// generator test in this suite can now be backed by a compile.
/// </para>
/// </summary>
internal static class GeneratorHarness
{
    /// <summary>
    /// CS0436 (generated type shadows an imported one) is the one code expected here:
    /// the "Exe B references Exe A's EndpointRegistrar" shape deliberately produces it,
    /// and it is a warning, not an error.
    /// </summary>
    internal const string ImportedTypeConflict = "CS0436";

    /// <summary>
    /// A path with a <c>/Controllers/</c> segment is what the generator's predicate looks
    /// for, so the default names a typical controller.
    /// </summary>
    internal const string TypicalControllerPath = "Controllers/PostsController.cs";

    /// <summary>
    /// The emitted endpoints file spells <c>CancellationToken</c> and <c>ValueTask</c>
    /// unqualified, exactly like the real consumer it targets: every project in this repo
    /// (samples, integration tests, the README samples) has <c>ImplicitUsings</c> enabled,
    /// so the SDK synthesises this file. Reproducing it keeps the harness measuring the
    /// generator instead of a project-property difference — a consumer with implicit usings
    /// switched off is a separate question, recorded in the task report.
    /// </summary>
    internal const string ImplicitUsingsSource = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    /// <summary>
    /// Assemblies the emitted code is known to need. Resolution is by file name from the
    /// runtime's own closure; a missing anchor throws instead of silently compiling the
    /// snippet against a partial world (which would surface as a confusing CS0246 in an
    /// unrelated test).
    /// </summary>
    private static readonly string[] RequiredAssemblies =
    [
        "System.Private.CoreLib.dll",
        "PicoNode.Web.dll",
        "PicoWeb.dll",
        "PicoJetson.dll",
    ];

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(
        BuildReferences
    );

    /// <summary>The full reference closure, shared by every test in this project.</summary>
    internal static ImmutableArray<MetadataReference> BaseReferences => References.Value;

    /// <summary>
    /// Builds a referenced assembly that already exports <c>EndpointRegistrar</c> — the
    /// "Exe B references Exe A (with controllers)" shape. Compiling against it is the
    /// only way to exercise the CS0436 exemption the harness makes.
    /// </summary>
    internal static MetadataReference ImportedRegistrarReference()
    {
        var tree = CSharpSyntaxTree.ParseText(
            "public static class EndpointRegistrar { public static void RegisterAll(object app) { } }"
        );
        // Same closure as the compiled tests: it already contains System.Private.CoreLib,
        // and reaching for typeof(object).Assembly.Location instead would add another
        // IL3000 to a project that is meant to stay trim-neutral.
        var compilation = CSharpCompilation.Create(
            "ImportedApp",
            [tree],
            BaseReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        if (!emit.Success)
            throw new InvalidOperationException("failed to emit the imported registrar assembly");
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    /// <summary>
    /// One generator run: the emitted text plus the compilation that already has the
    /// generated trees added, so a test can assert on both without running twice.
    /// </summary>
    /// <remarks>
    /// <see cref="GeneratorDiagnostics"/> is what the generator <em>reported</em>
    /// (<c>PCW001</c>…<c>PCW003</c>), a different channel from
    /// <see cref="CompilationDiagnostics"/>: a reported diagnostic never appears in the
    /// compilation's own diagnostics, so a rule asserted only through the compile would be a
    /// test that cannot see it at all.
    /// </remarks>
    internal sealed record GeneratedRun(
        CSharpCompilation Compilation,
        string Generated,
        ImmutableArray<Diagnostic> CompilationDiagnostics,
        ImmutableArray<Diagnostic> GeneratorDiagnostics
    );

    /// <summary>
    /// Runs <see cref="ControllersGenerator"/> over <paramref name="source"/> and returns
    /// the emitted text plus the CS0436 diagnostics that shape is allowed to produce.
    /// </summary>
    internal static (string Generated, IReadOnlyList<Diagnostic> Cs0436Diagnostics) RunGenerator(
        string source,
        string fileName,
        IReadOnlyList<MetadataReference>? extraReferences = null
    )
    {
        var run = RunGeneratorRun(source, fileName, extraReferences);
        var cs0436 = run.CompilationDiagnostics.Where(d => d.Id == ImportedTypeConflict).ToArray();
        return (run.Generated, cs0436);
    }

    /// <summary>
    /// Compiles the generated tree and returns every error, minus CS0436.
    /// <para>
    /// The work is synchronous — Roslyn has no async emit — so the returned
    /// <see cref="Task{TResult}"/> is already complete; awaiting it keeps the calling
    /// test free to stay async.
    /// </para>
    /// </summary>
    internal static Task<ImmutableArray<Diagnostic>> CompileGeneratedAsync(
        string source,
        string fileName = TypicalControllerPath,
        IReadOnlyList<MetadataReference>? extraReferences = null
    ) => CompileGeneratedAsync(RunGeneratorRun(source, fileName, extraReferences));

    /// <summary>
    /// Compiles a tree this suite already generated (keeps a test that also inspects the
    /// emitted text from running the generator twice).
    /// </summary>
    internal static Task<ImmutableArray<Diagnostic>> CompileGeneratedAsync(GeneratedRun run) =>
        CompileGeneratedAsync(run, DiagnosticSeverity.Error);

    /// <summary>
    /// Compiles a tree this suite already generated and returns every diagnostic at or above
    /// <paramref name="minimumSeverity"/>.
    /// <para>
    /// The <c>Warning</c> floor exists because "it compiles" is a weaker claim than it looks: a
    /// generated file can be error-free and still bury the consumer in warnings
    /// (<c>CS0219</c>, <c>CS8600</c>, …), which is a defect the error-only view cannot see.
    /// </para>
    /// </summary>
    internal static Task<ImmutableArray<Diagnostic>> CompileGeneratedAsync(
        GeneratedRun run,
        DiagnosticSeverity minimumSeverity
    )
    {
        if (run.Generated.Length == 0)
            throw new InvalidOperationException(
                "the generator emitted nothing for this source, so there is nothing to "
                    + "compile — a green compile here would be vacuous"
            );

        // The input compilation is a ConsoleApplication on purpose: the generator only
        // runs for executable output kinds. A ConsoleApplication without a Main is
        // CS5001, which would be a harness artefact rather than a generator bug — so the
        // emit happens as a library. Emit(Stream) writes no PDB, so no debug-information
        // stream is required either.
        var emitCompilation = run.Compilation.WithOptions(
            run.Compilation.Options.WithOutputKind(OutputKind.DynamicallyLinkedLibrary)
        );

        var emitResult = emitCompilation.Emit(Stream.Null);

        // EmitResult.Diagnostics is the full pipeline (parse → bind → emit) and therefore
        // a superset of GetDiagnostics(); de-duplicating by id+span keeps a diagnostic
        // reported twice from reading as two bugs.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<Diagnostic>();
        foreach (var diagnostic in emitResult.Diagnostics)
        {
            if (diagnostic.Severity < minimumSeverity)
                continue;
            if (diagnostic.Id == ImportedTypeConflict)
                continue;
            if (seen.Add(Key(diagnostic)))
                errors.Add(diagnostic);
        }

        return Task.FromResult(errors.ToImmutableArray());
    }

    /// <summary>Runs the generator and hands back everything a caller may need.</summary>
    internal static GeneratedRun RunGeneratorRun(
        string source,
        string fileName,
        IReadOnlyList<MetadataReference>? extraReferences = null
    )
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, ParseOptions, path: fileName);
        var implicitUsings = CSharpSyntaxTree.ParseText(
            ImplicitUsingsSource,
            ParseOptions,
            path: "TestAssembly.GlobalUsings.g.cs"
        );

        var references = extraReferences is { Count: > 0 }
            ? BaseReferences.AddRange(extraReferences)
            : BaseReferences;

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [syntaxTree, implicitUsings],
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)
        );

        var driver = CSharpGeneratorDriver.Create(new ControllersGenerator());
        var runResult = driver.RunGenerators(compilation).GetRunResult();

        var generatorDiagnostics = runResult.Results.IsDefaultOrEmpty
            ? ImmutableArray<Diagnostic>.Empty
            : runResult.Results[0].Diagnostics;

        if (runResult.Results.Length == 0 || runResult.Results[0].GeneratedSources.IsEmpty)
            return new GeneratedRun(
                compilation,
                "",
                ImmutableArray<Diagnostic>.Empty,
                generatorDiagnostics
            );

        var generatedSources = runResult.Results[0].GeneratedSources;
        var generated = string.Join("\n", generatedSources.Select(s => s.SourceText.ToString()));

        var updated = compilation.AddSyntaxTrees(generatedSources.Select(s => s.SyntaxTree));

        return new GeneratedRun(updated, generated, updated.GetDiagnostics(), generatorDiagnostics);
    }

    /// <summary>
    /// The source text a diagnostic points at, resolved against the source the test handed to the
    /// generator — the difference between "the diagnostic has a location" and "the diagnostic
    /// points at the offending identifier": a location covering the whole file would satisfy the
    /// first and fail the second.
    /// <para>
    /// The location is resolved through <see cref="Location.SourceSpan"/> rather than through
    /// <see cref="Location.SourceTree"/> because a reported location is an <em>external file</em>
    /// one: the model travels down an incremental pipeline, and a model that carried a
    /// <see cref="SyntaxTree"/> could not be cached (see the task report). The span is still an
    /// exact offset into the file the user wrote, which is what navigation needs.
    /// </para>
    /// </summary>
    internal static string TextAt(Diagnostic diagnostic, string source)
    {
        var span = diagnostic.Location.SourceSpan;
        if (span.Start < 0 || span.End > source.Length)
            return "<span outside the source>";

        return source.Substring(span.Start, span.Length);
    }

    private static string Key(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return string.Join(
            "|",
            diagnostic.Id,
            span.Path,
            span.StartLinePosition.Line.ToString(CultureInfo.InvariantCulture),
            span.StartLinePosition.Character.ToString(CultureInfo.InvariantCulture),
            diagnostic.GetMessage(CultureInfo.InvariantCulture)
        );
    }

    /// <summary>
    /// Builds the reference set from the runtime's own closure
    /// (<c>TRUSTED_PLATFORM_ASSEMBLIES</c>), not from a hand-picked list of
    /// <c>typeof(...).Assembly.Location</c> values:
    /// <list type="bullet">
    /// <item><c>Assembly.Location</c> is empty in a single-file or bundled host (IL3000,
    /// which this project already trips eleven times), so a hand-built list silently
    /// degrades to "fewer references than the test needs" there.</item>
    /// <item><c>AppDomain.CurrentDomain.GetAssemblies()</c> only lists what happens to be
    /// loaded — <c>PicoNode.Web</c> may never have been touched in a given run — so the
    /// reference set would depend on test order.</item>
    /// <item>A hand-picked list is always one assembly short of the next generated
    /// signature: <c>PicoSerDe.Core</c>, <c>PicoDI</c>, <c>PicoDI.Abs</c> and
    /// <c>PicoNode.Abs</c> only appear transitively, and a missing one reads as CS0246 — a
    /// harness bug wearing a generator bug's clothes.</item>
    /// </list>
    /// </summary>
    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var paths = TrustedPlatformAssemblies();
        if (paths.Length == 0)
            paths = AssemblyFilesBesideTheTestBinary();

        // One reference per simple name: a path can be listed twice, and a duplicate
        // reference is a build error in Roslyn.
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (!byName.ContainsKey(name))
                byName[name] = path;
        }

        foreach (var required in RequiredAssemblies)
        {
            if (!byName.ContainsKey(required))
                throw new InvalidOperationException(
                    $"The compile harness cannot see {required}. It resolves references from "
                        + $"TRUSTED_PLATFORM_ASSEMBLIES ({paths.Length} entries) and falls back to "
                        + $"'{AppContext.BaseDirectory}'. Without it the generated tree would be "
                        + "compiled against a partial world."
                );
        }

        return
        [
            .. byName
                .Values.Order(StringComparer.OrdinalIgnoreCase)
                .Select(path => MetadataReference.CreateFromFile(path)),
        ];
    }

    private static string[] TrustedPlatformAssemblies()
    {
        var raw = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrEmpty(raw))
            return [];

        return
        [
            .. raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(File.Exists),
        ];
    }

    /// <summary>Last resort when the host exports no trusted-assembly list.</summary>
    private static string[] AssemblyFilesBesideTheTestBinary()
    {
        var baseDirectory = AppContext.BaseDirectory;
        return Directory.Exists(baseDirectory)
            ? [.. Directory.EnumerateFiles(baseDirectory, "*.dll")]
            : [];
    }
}
