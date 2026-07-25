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
        await Assert.That(result).DoesNotContain("GetService(typeof(global::PicoNode.Web.WebContext))");
        await Assert.That(result).DoesNotContain("GetService(typeof(global::System.Threading.CancellationToken))");
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
    public async Task Int_route_param_uses_intParse_not_ConvertChangeType()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public string GetUser(int id) { return "test"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        await Assert.That(result).Contains("int.Parse(ctx.RouteValues[\"id\"]");
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
    public async Task Long_route_param_uses_longParse()
    {
        var source = """
            namespace MyApp.Controllers;
            public class ItemsController
            {
                public string GetItem(long id) { return "ok"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/ItemsController.cs");

        await Assert.That(result).Contains("long.Parse(ctx.RouteValues[\"id\"]");
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
        await Assert.That(result).Contains("int.Parse(ctx.RouteValues[\"blogId\"]");
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
        await Assert.That(result).Contains("scope.GetService(typeof(global::PicoNode.Web.HtmlResult))");
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
        await Assert.That(result).DoesNotContain(
            "SvcDescriptor.Create(typeof(global::TestApp.Controllers.StaticController)");
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
    public async Task Guid_parameter_uses_Guid_Parse_not_Convert_ChangeType()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public string GetUser(System.Guid id) { return "test"; }
            }
            """;

        var result = RunGenerator(source, "Controllers/UsersController.cs");

        // Debug: show generated source for route binding
        await Assert.That(result).Contains("Guid.Parse");
        await Assert.That(result).DoesNotContain("Convert.ChangeType");
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

    private static string RunGenerator(string source, string fileName)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
            path: fileName
        );

        var references = new[]
        {
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
        };

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)
        );

        var generator = new ControllersGenerator();

        // Create driver, run generators, get results
        var driver = CSharpGeneratorDriver.Create(generator);
        var runResult = driver.RunGenerators(compilation).GetRunResult();

        // Collect all generated source texts
        if (runResult.Results.Length == 0)
            return "";

        var sources = runResult.Results[0].GeneratedSources;
        if (sources.IsEmpty)
            return "";

        return string.Join("\n", sources.Select(s => s.SourceText.ToString()));
    }
}
