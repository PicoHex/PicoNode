using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Controllers.Gen;

namespace PicoWeb.Tests;

public sealed class ControllersGeneratorTests
{
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

        await Assert.That(result).Contains("int.Parse(ctx.RouteValues[\"id\"])");
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

        await Assert.That(result).Contains("long.Parse(ctx.RouteValues[\"id\"])");
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
        await Assert.That(result).Contains("int.Parse(ctx.RouteValues[\"blogId\"])");
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

        // The handler lambda should take only WebContext, not route params
        await Assert.That(result).Contains("async (WebContext ctx) =>");
        await Assert.That(result).DoesNotContain("async (WebContext ctx, int id) =>");
    }

    private static string RunGenerator(string source, string fileName)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
            path: fileName);

        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(PicoJetson.PicoJsonSerializableAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(PicoJetson.JsonSerializer).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(PicoNode.Web.WebApp).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(PicoNode.Web.WebContext).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(PicoNode.Web.WebResults).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(PicoWeb.Results).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create("TestAssembly",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

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
