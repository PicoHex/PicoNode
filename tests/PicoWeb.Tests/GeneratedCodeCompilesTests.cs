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
    /// <b>RED on purpose.</b> <c>Create(CreatePostDto dto)</c> takes a complex type, so
    /// the generator binds it with an uncast
    /// <c>var __dto = ctx.Services.GetService(typeof(…));</c> — <c>object</c> passed to a
    /// <c>CreatePostDto</c> parameter is CS1503. The constructor path
    /// (<c>ControllersGenerator.cs</c>, the <c>scope.GetService</c> branch) already
    /// casts, and the fix is the same one-line cast on the action-parameter path
    /// (F-51 → the next task). This test is that task's RED and is deliberately not
    /// skipped.
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
