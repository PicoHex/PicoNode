namespace PicoWeb.Tests;

public sealed class ControllersGeneratorTests
{
    [Test]
    public async Task ControllerWithWebContextParam_GeneratesCtxArg()
    {
        var source = """
            using PicoNode.Web;
            using System.Threading;
            namespace TestApp.Controllers;
            public class TestController
            {
                public HtmlResult GetPage(WebContext ctx, CancellationToken ct)
                    => new HtmlResult("<h1>Hello</h1>");
            }
            """;

        var result = RunGenerator(source, "Controllers/TestController.cs");

        await Assert.That(result).Contains("GetPage(ctx, ct)");
        await Assert
            .That(result)
            .DoesNotContain("GetService(typeof(global::PicoNode.Web.WebContext))");
        await Assert
            .That(result)
            .DoesNotContain("GetService(typeof(global::System.Threading.CancellationToken))");
    }

    [Test]
    public async Task ControllerWithAbsoluteRoute_DoesNotDuplicatePrefix()
    {
        var source = """
            using PicoNode.Web;
            namespace TestApp.Controllers;
            [Route("/")]
            public class TestController
            {
                [HttpGet("/")]
                public HtmlResult GetIndex() => new HtmlResult("<h1>Hello</h1>");
            }
            """;

        var result = RunGenerator(source, "Controllers/TestController.cs");

        // Should produce route "/" not "//"
        await Assert.That(result).Contains("MapGet(\"/\"");
        await Assert.That(result).DoesNotContain("MapGet(\"//\"");
    }

    [Test]
    public async Task Controller_in_Controllers_folder_generates_EndpointRegistrar()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public string GetUser(int id) { return "test"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        await Assert.That(result).IsNotEmpty();
        await Assert.That(result).Contains("public static class EndpointRegistrar");
        await Assert.That(result).Contains("UsersController_Endpoints.Register");
    }

    [Test]
    public async Task Controller_with_DTO_return_does_not_need_serializable_marker()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public UserDto GetUser(int id) { return new UserDto(); }
            }
            public class UserDto { public string Name { get; set; } }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        // Controllers.Gen no longer generates [PicoJsonSerializable] markers.
        // Users must apply the attribute directly to DTOs for PicoJetson.Gen to discover.
        await Assert.That(result).DoesNotContain("PicoJsonSerializable");
    }

    [Test]
    public async Task File_outside_Controllers_folder_only_has_EndpointRegistrar()
    {
        var source = """
            namespace MyApp;
            public class NotAController
            {
                public string GetSomething() { return "ok"; }
            }
            """;

        var result = RunGenerator(source, "Models/NotAController.cs");

        // EndpointRegistrar is always generated (even empty)
        await Assert.That(result).Contains("EndpointRegistrar");
        await Assert.That(result).DoesNotContain("MapGet");
    }

    [Test]
    public async Task No_controllers_with_imported_registrar_emits_nothing()
    {
        var source = """
            namespace MyApp;
            public class NotAController
            {
                public string GetSomething() { return "ok"; }
            }
            """;

        var result = RunGenerator(
            source,
            "Models/NotAController.cs",
            [CreateAssemblyWithEndpointRegistrar()]
        );

        // An Exe referencing another app that already carries the registrar must
        // not emit its own: the local public shim shadows the imported real one
        // (CS0436) and the referenced app's controllers would never register.
        await Assert.That(result).DoesNotContain("EndpointRegistrar");
    }

    [Test]
    public async Task No_controllers_without_imported_registrar_keeps_empty_shim()
    {
        var source = """
            namespace MyApp;
            public class NotAController { }
            """;

        var result = RunGenerator(source, "Models/NotAController.cs");

        // Standalone projects (no controllers, no imported registrar) keep the
        // shim so EndpointRegistrar.RegisterAll(app) still compiles as a no-op.
        await Assert.That(result).Contains("public static class EndpointRegistrar");
    }

    [Test]
    public async Task Controllers_with_imported_registrar_still_emit_own_registrar()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public string GetUser(int id) { return "test"; }
            }
            """;

        var result = RunGenerator(
            source,
            "Controllers/UsersController.cs",
            [CreateAssemblyWithEndpointRegistrar()]
        );

        // A project that owns controllers must keep registering them: an
        // imported registrar cannot know about this assembly's endpoints.
        await Assert.That(result).Contains("public static class EndpointRegistrar");
        await Assert.That(result).Contains("UsersController_Endpoints.Register");
    }

    [Test]
    public async Task No_controllers_with_imported_registrar_does_not_reproduce_CS0436()
    {
        var source = """
            namespace MyApp;
            public class NotAController { }
            """;

        var (generated, cs0436) = RunGeneratorWithDiagnostics(
            source,
            "Models/NotAController.cs",
            [CreateAssemblyWithEndpointRegistrar()]
        );

        await Assert.That(generated).DoesNotContain("EndpointRegistrar");
        await Assert.That(cs0436.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Get_method_with_int_param_generates_route_containing_id_placeholder()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public string GetUser(int id) { return "test"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        await Assert.That(result).Contains("/api/users/user/{id}");
    }

    [Test]
    public async Task Get_method_generates_MapGet_not_MapGET()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public string GetUser(int id) { return "test"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        await Assert.That(result).Contains("app.MapGet(");
        await Assert.That(result).DoesNotContain("app.MapGET(");
    }

    [Test]
    public async Task Post_method_generates_MapPost()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public void PostUser(string name) { }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        await Assert.That(result).Contains("app.MapPost(");
    }

    [Test]
    public async Task Generated_code_uses_fully_qualified_names()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public string GetUser(int id) { return "test"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        // Controller type should be globally qualified
        await Assert.That(result).Contains("typeof(global::MyApp.Controllers.UsersController)");
        await Assert.That(result).Contains("(global::MyApp.Controllers.UsersController)");
    }

    [Test]
    public async Task Int_route_param_uses_int_TryParse_guard()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public string GetUser(int id) { return "test"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        await Assert.That(result).Contains("int.TryParse(ctx.RouteValues[\"id\"]");
        await Assert.That(result).DoesNotContain("Convert.ChangeType");
    }

    [Test]
    public async Task String_route_param_is_assigned_directly()
    {
        var source = """
            namespace MyApp.Controllers;
            public class PostsController
            {
                public string GetPost(string slug) { return slug; }
            }
            """;

        var result = RunGenerator(source, "Controllers/PostsController.cs");

        await Assert.That(result).Contains("var __slug = ctx.RouteValues[\"slug\"]");
    }

    [Test]
    public async Task Long_route_param_uses_long_TryParse_guard()
    {
        var source = """
            namespace MyApp.Controllers;
            public class ItemsController
            {
                public string GetItem(long id) { return "ok"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/ItemsController.cs");

        await Assert.That(result).Contains("long.TryParse(ctx.RouteValues[\"id\"]");
        await Assert.That(result).Contains("PicoWeb.Results.Empty(404)");
    }

    [Test]
    public async Task Async_Task_T_return_generates_await()
    {
        var source = """
            namespace MyApp.Controllers;
            using System.Threading.Tasks;
            public class UsersController
            {
                public Task<UserDto> GetUser(int id) { return Task.FromResult(new UserDto()); }
            }
            public class UserDto { public string Name { get; set; } }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        await Assert.That(result).Contains("await");
        // Should unwrap Task<T> to the inner type
        await Assert.That(result).Contains("(global::MyApp.Controllers.UserDto)await");
    }

    [Test]
    public async Task Async_ValueTask_T_return_generates_await()
    {
        var source = """
            namespace MyApp.Controllers;
            using System.Threading.Tasks;
            public class UsersController
            {
                public ValueTask<UserDto> GetUser(int id) { return ValueTask.FromResult(new UserDto()); }
            }
            public class UserDto { public string Name { get; set; } }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        await Assert.That(result).Contains("await");
        await Assert.That(result).Contains("(global::MyApp.Controllers.UserDto)await");
    }

    [Test]
    public async Task Multiple_route_params_all_generated()
    {
        var source = """
            namespace MyApp.Controllers;
            public class PostsController
            {
                public string GetPost(int blogId, string slug) { return slug; }
            }
            """;

        var result = RunGenerator(source, "Controllers/PostsController.cs");

        await Assert.That(result).Contains("{blogId}");
        await Assert.That(result).Contains("{slug}");
        await Assert.That(result).Contains("int.TryParse(ctx.RouteValues[\"blogId\"]");
        await Assert.That(result).Contains("var __slug = ctx.RouteValues[\"slug\"]");
    }

    [Test]
    public async Task Void_return_does_not_serialize()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public void DeleteUser(int id) { }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        await Assert.That(result).Contains("app.MapDelete(");
        // void methods: the return type should be plain "void"
    }

    [Test]
    public async Task Delete_method_generates_MapDelete()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public void DeleteUser(int id) { }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        await Assert.That(result).Contains("app.MapDelete(");
    }

    [Test]
    public async Task Patch_method_generates_MapPatch()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public string PatchUser(int id) { return "ok"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        await Assert.That(result).Contains("app.MapPatch(");
    }

    [Test]
    public async Task HttpPatch_Attribute_GeneratesMapPatch()
    {
        var source = """
            using PicoNode.Web;
            namespace TestApp.Controllers;
            public class TestController
            {
                [HttpPatch("{id}")]
                public HtmlResult PatchResource(int id) => new HtmlResult("ok");
            }
            """;

        var result = RunGenerator(source, "Controllers/TestController.cs");

        await Assert.That(result).Contains("app.MapPatch(");
        await Assert.That(result).Contains("MapPatch(\"/api/test/{id}\"");
    }

    [Test]
    public async Task HttpPost_EmptyAttribute_NoTrailingSlash()
    {
        var source = """
            using PicoNode.Web;
            namespace TestApp.Controllers;
            public class TestController
            {
                [HttpPost]
                public HtmlResult Post() => new HtmlResult("ok");
            }
            """;

        var result = RunGenerator(source, "Controllers/TestController.cs");

        // Should be "/api/test" not "/api/test/"
        await Assert.That(result).Contains("MapPost(\"/api/test\"");
        await Assert.That(result).DoesNotContain("MapPost(\"/api/test/\"");
    }

    [Test]
    public async Task HttpGet_EmptyAttribute_UsesControllerPrefix()
    {
        var source = """
            using PicoNode.Web;
            namespace TestApp.Controllers;
            public class TestController
            {
                [HttpGet]
                public HtmlResult GetList() => new HtmlResult("ok");
            }
            """;

        var result = RunGenerator(source, "Controllers/TestController.cs");

        // Should be "/api/test" not "/api/test/list"
        await Assert.That(result).Contains("MapGet(\"/api/test\"");
        await Assert.That(result).DoesNotContain("MapGet(\"/api/test/list\"");
    }

    [Test]
    public async Task RoutePrefix_is_api_controllers_kebab()
    {
        var source = """
            namespace MyApp.Controllers;
            public class BlogPostsController
            {
                public string GetRecent(int count) { return "ok"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/BlogPostsController.cs");

        // BlogPostsController → /api/blog-posts
        await Assert.That(result).Contains("/api/blog-posts/recent/{count}");
    }

    [Test]
    public async Task Method_name_containing_param_substring_not_truncated()
    {
        var source = """
            namespace MyApp.Controllers;
            public class WidgetsController
            {
                public string GetWidget(int id) { return "test"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/WidgetsController.cs");

        // Regression: "Widget".IndexOf("id") matched mid-word (W-i-d-g-e-t)
        // and truncated the route to "widg". Only a trailing parameter-name
        // suffix may be stripped.
        await Assert.That(result).Contains("/api/widgets/widget/{id}");
        await Assert.That(result).DoesNotContain("/api/widgets/widg/{id}");
    }

    [Test]
    public async Task Method_without_prefix_http_verb_skipped()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public string Help() { return "help"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        // Help doesn't start with Get/Post/Put/Delete/Patch — should NOT be registered
        await Assert.That(result).DoesNotContain("MapGet");
    }

    [Test]
    public async Task Controller_exposes_lambda_takes_only_WebContext()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public string GetUser(int id) { return "test"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        // The handler lambda uses WebRequestHandler signature (ctx + CancellationToken),
        // not route params as lambda parameters
        await Assert.That(result).Contains("(WebContext ctx, CancellationToken ct) =>");
        await Assert.That(result).DoesNotContain("int id");
    }

    [Test]
    public async Task Controller_generates_DI_registration()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public string GetUser(int id) { return "test"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        // Should generate DI registration with ModuleInitializer
        await Assert.That(result).Contains("ControllerServiceRegistrations");
        await Assert.That(result).Contains("SvcDescriptor.Create");
        await Assert.That(result).Contains("ModuleInitializer");
        await Assert.That(result).Contains("SvcContainerAutoConfiguration.RegisterConfigurator");
        await Assert.That(result).Contains("typeof(global::MyApp.Controllers.UsersController)");
        await Assert.That(result).Contains("new global::MyApp.Controllers.UsersController()");
        await Assert.That(result).Contains("SvcLifetime.Scoped");
    }

    [Test]
    public async Task ControllerWithParameterizedCtor_GeneratesScopedFactory()
    {
        var source = """
            using PicoNode.Web;
            namespace TestApp.Controllers;
            public class TestController
            {
                private readonly HtmlResult _page;
                public TestController(HtmlResult page) => _page = page;
                public HtmlResult GetPage() => _page;
            }
            """;

        var result = RunGenerator(source, "Controllers/TestController.cs");

        // Should generate SvcDescriptor.Create with factory delegate
        await Assert.That(result).Contains("SvcDescriptor.Create");
        await Assert.That(result).Contains("scope =>");
        await Assert
            .That(result)
            .Contains("scope.GetService(typeof(global::PicoNode.Web.HtmlResult))");
        await Assert.That(result).Contains("new global::TestApp.Controllers.TestController(");
        await Assert.That(result).Contains("SvcLifetime.Scoped");
    }

    [Test]
    public async Task StaticController_NotRegisteredInDI()
    {
        var source = """
            using PicoNode.Web;
            namespace TestApp.Controllers;
            public static class StaticController
            {
                public static HtmlResult GetPage() => new HtmlResult("ok");
            }
            """;

        var result = RunGenerator(source, "Controllers/StaticController.cs");

        // Should NOT contain DI registration for StaticController
        await Assert
            .That(result)
            .DoesNotContain(
                "SvcDescriptor.Create(typeof(global::TestApp.Controllers.StaticController)"
            );
        // But should still generate endpoint
        await Assert.That(result).Contains("MapGet");
    }

    [Test]
    public async Task ControllerWithParameterlessCtor_UsesStaticFactory()
    {
        var source = """
            using PicoNode.Web;
            namespace TestApp.Controllers;
            public class TestController
            {
                public TestController() { }
                public HtmlResult GetPage() => new HtmlResult("ok");
            }
            """;

        var result = RunGenerator(source, "Controllers/TestController.cs");

        // Should use static _ => new() pattern (backward compat)
        await Assert.That(result).Contains("static _ => new");
        await Assert.That(result).Contains("SvcDescriptor.Create");
    }

    [Test]
    public async Task Async_Task_T_method_generates_async_lambda_with_await()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public async System.Threading.Tasks.Task<string> GetUser(int id) { return "test"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        // Must emit 'async' on the lambda so 'await' compiles (CS4001 fix).
        await Assert.That(result).Contains("async (WebContext ctx, CancellationToken ct) =>");
        await Assert.That(result).Contains("await");
        // Async path should not wrap in ValueTask.FromResult — return directly.
        await Assert.That(result).DoesNotContain("ValueTask.FromResult");
    }

    [Test]
    public async Task Guid_parameter_uses_Guid_TryParse_not_Convert_ChangeType()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public string GetUser(System.Guid id) { return "test"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        // TryParse guard — an unparseable segment (e.g. img.png) must 404,
        // never throw FormatException → 500.
        await Assert.That(result).Contains("Guid.TryParse");
        await Assert.That(result).Contains("PicoWeb.Results.Empty(404)");
        await Assert.That(result).DoesNotContain("Guid.Parse(");
        await Assert.That(result).DoesNotContain("Convert.ChangeType");
    }

    [Test]
    public async Task Guid_parameter_unparseable_route_value_returns_404()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public string GetUser(System.Guid id) { return "test"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        // Regression: GET /fragments/sessions/img.png matched {id} and threw
        // System.FormatException (Guid.Parse) → 500. The guard must bind via
        // out-var TryParse and short-circuit with 404.
        await Assert.That(result).Contains("out var __id");
        await Assert
            .That(result)
            .Contains("return ValueTask.FromResult(PicoWeb.Results.Empty(404));");
    }

    [Test]
    public async Task Int_parameter_unparseable_route_value_returns_404()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public string GetUser(int id) { return "test"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        await Assert.That(result).Contains("int.TryParse");
        await Assert.That(result).Contains("System.Globalization.CultureInfo.InvariantCulture");
        await Assert.That(result).Contains("PicoWeb.Results.Empty(404)");
        await Assert.That(result).DoesNotContain("int.Parse(");
    }

    [Test]
    public async Task Enum_parameter_unparseable_route_value_returns_404()
    {
        var source = """
            namespace MyApp.Controllers;
            public enum Color { Red, Green }
            public class UsersController
            {
                public string GetUser(Color color) { return "test"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        await Assert.That(result).Contains("System.Enum.TryParse");
        await Assert.That(result).Contains("PicoWeb.Results.Empty(404)");
    }

    [Test]
    public async Task String_parameter_has_no_parse_guard()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public string GetUser(string id) { return "test"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        // Strings bind raw — nothing can fail, so no 404 guard may appear.
        await Assert.That(result).Contains("var __id = ctx.RouteValues[\"id\"];");
        await Assert.That(result).DoesNotContain("PicoWeb.Results.Empty(404)");
    }

    [Test]
    public async Task ControllerReturningIWebResult_GeneratesExecuteCall()
    {
        var source = """
            using PicoNode.Web;
            namespace TestApp.Controllers;
            public class TestController
            {
                public HtmlResult GetPage() => new HtmlResult("<h1>Hello</h1>");
            }
            """;

        var result = RunGenerator(source, "Controllers/TestController.cs");

        await Assert.That(result).Contains(".Execute(ctx)");
        await Assert.That(result).DoesNotContain("JsonSerializer.SerializeToUtf8Bytes");
    }

    [Test]
    public async Task ControllerReturningIWebResult_AsyncTask_GeneratesExecuteCall()
    {
        var source = """
            using PicoNode.Web;
            using System.Threading.Tasks;
            namespace TestApp.Controllers;
            public class TestController
            {
                public Task<HtmlResult> GetPage() => Task.FromResult(new HtmlResult("<h1>Hello</h1>"));
            }
            """;

        var result = RunGenerator(source, "Controllers/TestController.cs");

        await Assert.That(result).Contains(".Execute(ctx)");
        await Assert.That(result).DoesNotContain("JsonSerializer.SerializeToUtf8Bytes");
    }

    private static string RunGenerator(
        string source,
        string fileName,
        IReadOnlyList<MetadataReference>? extraReferences = null
    ) => RunGeneratorCore(source, fileName, extraReferences ?? []).Generated;

    private static (
        string Generated,
        IReadOnlyList<Diagnostic> Cs0436Diagnostics
    ) RunGeneratorWithDiagnostics(
        string source,
        string fileName,
        IReadOnlyList<MetadataReference> extraReferences
    ) => RunGeneratorCore(source, fileName, extraReferences);

    private static (string Generated, IReadOnlyList<Diagnostic> Cs0436Diagnostics) RunGeneratorCore(
        string source,
        string fileName,
        IReadOnlyList<MetadataReference> extraReferences
    )
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
            path: fileName
        );

        var references = BaseReferences().Concat(extraReferences).ToArray();

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)
        );

        var driver = CSharpGeneratorDriver.Create(new ControllersGenerator());
        var runResult = driver.RunGenerators(compilation).GetRunResult();

        if (runResult.Results.Length == 0 || runResult.Results[0].GeneratedSources.IsEmpty)
            return ("", []);

        var sources = runResult.Results[0].GeneratedSources;
        var generated = string.Join("\n", sources.Select(s => s.SourceText.ToString()));

        var updated = compilation.AddSyntaxTrees(sources.Select(s => s.SyntaxTree));
        var cs0436 = updated.GetDiagnostics().Where(d => d.Id == "CS0436").ToArray();

        return (generated, cs0436);
    }

    private static MetadataReference[] BaseReferences() =>
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(
                typeof(System.Collections.Generic.List<>).Assembly.Location
            ),
            MetadataReference.CreateFromFile(
                typeof(PicoJetson.PicoJsonSerializableAttribute).Assembly.Location
            ),
            MetadataReference.CreateFromFile(typeof(PicoJetson.JsonSerializer).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(PicoNode.Web.WebApp).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(PicoNode.Web.WebContext).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(PicoNode.Web.WebResults).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(PicoWeb.Results).Assembly.Location),
        ];

    /// <summary>
    /// Builds a referenced assembly that already exports EndpointRegistrar — the
    /// "Exe B references Exe A (with controllers)" shape.
    /// </summary>
    private static MetadataReference CreateAssemblyWithEndpointRegistrar()
    {
        var tree = CSharpSyntaxTree.ParseText(
            "public static class EndpointRegistrar { public static void RegisterAll(object app) { } }"
        );
        var compilation = CSharpCompilation.Create(
            "ImportedApp",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        if (!emit.Success)
            throw new InvalidOperationException("failed to emit the imported registrar assembly");
        return MetadataReference.CreateFromImage(stream.ToArray());
    }
}
