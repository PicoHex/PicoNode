using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace PicoWeb.Tests;

/// <summary>
/// Compiles what <see cref="ControllersGenerator"/> emits instead of matching strings
/// in it. The rest of the generator suite asserts text, which cannot see the failure
/// mode this file is for: emitted code that does not compile (audit F-51…F-53 —
/// CS1503 / CS0716 / CS0246 / CS0101).
/// <para>
/// A test here fails when <c>CompileGeneratedAsync</c> reports any error other than
/// CS0436. If the generator emits nothing at all, the harness throws: a green compile
/// of an empty tree would be vacuous.
/// </para>
/// </summary>
public sealed class GeneratedCodeCompilesTests
{
    /// <summary>
    /// The controller from the audit: a POST action taking a DTO and returning one.
    /// <para>
    /// <b>This is F-51's regression test.</b> <c>Create(CreatePostDto dto)</c> takes a
    /// complex type, and <c>ISvcScope.GetService</c> returns <c>object?</c> — so the
    /// generator must cast the resolved service before handing it to the typed
    /// parameter, or the emitted line is
    /// <c>var __dto = ctx.Services.GetService(…);</c> followed by
    /// <c>Create(__dto)</c>, i.e. <c>object</c> to <c>CreatePostDto</c>: CS1503. The
    /// constructor path already cast; the action-parameter path now does too. Removing
    /// that cast turns this test red again — that mutation is the proof it still bites.
    /// </para>
    /// </summary>
    [Test]
    public async Task Generated_code_for_a_typical_controller_compiles()
    {
        var source = """
            using PicoNode.Web;

            namespace TestApp.Controllers;

            public sealed class CreatePostDto
            {
                public string Title { get; set; } = "";
            }

            public sealed class PostDto
            {
                public int Id { get; set; }
            }

            public sealed class PostsController
            {
                [HttpPost]
                public PostDto Create(CreatePostDto dto) => new PostDto();
            }
            """;

        var errors = await GeneratorHarness.CompileGeneratedAsync(source);

        await Assert.That(Describe(errors)).IsEmpty();
    }

    /// <summary>
    /// The exemption the harness makes must be exactly one code, not a blind spot: a
    /// project that owns controllers <em>and</em> references an assembly that already
    /// exports <c>EndpointRegistrar</c> legitimately produces CS0436 (generated type
    /// shadows an imported one) and must still compile for real errors. This test is the
    /// control for the red one above — it fails the moment the harness stops reporting
    /// anything, which is the mutation that would otherwise let the red test pass.
    /// </summary>
    [Test]
    public async Task Controller_project_beside_an_imported_registrar_still_compiles()
    {
        var source = """
            using PicoNode.Web;

            namespace TestApp.Controllers;

            public sealed class UsersController
            {
                [HttpGet("users")]
                public string GetUser(int id) => "test";
            }

            public static class Program
            {
                public static void Main() => EndpointRegistrar.RegisterAll(null!);
            }
            """;

        var run = GeneratorHarness.RunGeneratorRun(
            source,
            "Controllers/UsersController.cs",
            [GeneratorHarness.ImportedRegistrarReference()]
        );

        // The exemption is only meaningful if CS0436 is genuinely there: without this
        // assertion the test would still pass if the shape stopped producing it, and the
        // "CS0436 is filtered" line would be untested.
        await Assert
            .That(
                run.CompilationDiagnostics.Any(d => d.Id == GeneratorHarness.ImportedTypeConflict)
            )
            .IsTrue();

        var errors = await GeneratorHarness.CompileGeneratedAsync(run);

        await Assert.That(Describe(errors)).IsEmpty();
    }

    /// <summary>
    /// A <c>static</c> controller has no instance to resolve — the generator deliberately
    /// never registers it in DI (<c>ControllersGeneratorTests.StaticController_NotRegisteredInDI</c>),
    /// so the endpoint must call the type directly:
    /// <c>global::TestApp.Controllers.StatusController.Get()</c>.
    /// <para>
    /// <b>This is F-52's regression test.</b> The instance shape
    /// <c>var __controller = ctx.Services.GetService(typeof(StatusController))!;</c> followed
    /// by <c>((StatusController)__controller).Get()</c> is two defects in one line: CS0716
    /// (a static type cannot be the target of a cast) makes the emitted tree uncompilable,
    /// and the resolve would answer <c>null</c> at run time precisely because the type is
    /// never registered.
    /// </para>
    /// <para>
    /// The second shape below is the same bug one level down and is pinned here on purpose:
    /// a <c>static</c> method on an ordinary (instance) controller was also emitted against
    /// <c>__controller</c>, which is CS0176 ("cannot be accessed with an instance
    /// reference"). Fixing F-52 on <c>ClassDeclarationSymbol.IsStatic</c> alone leaves that
    /// half broken and this half untested, so the discriminator is the member
    /// (<c>IMethodSymbol.IsStatic</c>) and both shapes are asserted from one generator run
    /// each.
    /// </para>
    /// </summary>
    [Test]
    public async Task Static_controller_compiles_and_is_invoked_statically()
    {
        var staticClassSource = """
            using PicoNode.Web;

            namespace TestApp.Controllers;

            public static class StatusController
            {
                [HttpGet("status")]
                public static string Get() => "ok";
            }
            """;

        // One run, two views of it: the emitted text and the compilation of that same text
        // must never come from two different generator runs.
        var run = GeneratorHarness.RunGeneratorRun(
            staticClassSource,
            "Controllers/StatusController.cs"
        );

        // The compile comes first so a regression reports CS0716 — the actual defect —
        // instead of a missing-substring assertion standing in for it.
        var errors = await GeneratorHarness.CompileGeneratedAsync(run);
        await Assert.That(Describe(errors)).IsEmpty();

        var generated = run.Generated;
        await Assert
            .That(generated)
            .Contains("global::TestApp.Controllers.StatusController.Get();");
        // The two halves of the shape, asserted separately so a regression names which one
        // came back: the cast, and the DI resolve of an instance that never exists.
        await Assert
            .That(generated)
            .DoesNotContain("(global::TestApp.Controllers.StatusController)");
        await Assert
            .That(generated)
            .DoesNotContain("GetService(typeof(global::TestApp.Controllers.StatusController))");

        // Same discriminator, one level down: a static action on an instance controller.
        var staticMethodSource = """
            using PicoNode.Web;

            namespace TestApp.Controllers;

            public sealed class MiscController
            {
                [HttpGet("ping")]
                public static string Ping() => "pong";
            }
            """;

        var staticMethodRun = GeneratorHarness.RunGeneratorRun(
            staticMethodSource,
            "Controllers/MiscController.cs"
        );

        var staticMethodErrors = await GeneratorHarness.CompileGeneratedAsync(staticMethodRun);
        await Assert.That(Describe(staticMethodErrors)).IsEmpty();
        await Assert
            .That(staticMethodRun.Generated)
            .Contains("global::TestApp.Controllers.MiscController.Ping();");
    }

    private static string Describe(ImmutableArray<Diagnostic> errors) =>
        errors.IsEmpty
            ? ""
            : string.Join(
                "\n",
                errors.Select(d =>
                    $"{d.Id} {d.Location.GetLineSpan().Path}({d.Location.GetLineSpan().StartLinePosition.Line + 1}): {d.GetMessage()}"
                )
            );
}
