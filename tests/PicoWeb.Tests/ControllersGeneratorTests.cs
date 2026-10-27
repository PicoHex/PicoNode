using System.Text.RegularExpressions;

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

        // Route value first, query string second (audit F-02); only then
        // does the raw located string go through TryParse.
        await Assert.That(result).Contains("ctx.RouteValues.TryGetValue(\"id\", out var __id_raw)");
        await Assert.That(result).Contains("&& !ctx.Query.TryGetValue(\"id\", out __id_raw)");
        await Assert.That(result).Contains("int.TryParse(__id_raw");
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

        // A string binds raw (no TryParse) from the route value or the
        // query string.
        await Assert.That(result).Contains("out var __slug)");
        await Assert.That(result).Contains("&& !ctx.Query.TryGetValue(\"slug\", out __slug)");
        await Assert.That(result).Contains("__controller).GetPost(__slug);");
        await Assert.That(result).DoesNotContain("TryParse");
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

        await Assert.That(result).Contains("long.TryParse(__id_raw");
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
        await Assert.That(result).Contains("int.TryParse(__blogId_raw");
        await Assert.That(result).Contains("out var __slug)");
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

        // ...and it must call the static type directly (audit F-52). The instance shape
        // is CS0716 for a static class — and the DI resolve it rests on would return
        // null, since the assertion above is exactly what keeps the type unregistered.
        // The compile-side witness is GeneratedCodeCompilesTests
        // .Static_controller_compiles_and_is_invoked_statically; this keeps the text
        // assertion beside the DI one so the two halves of the shape stay together.
        await Assert
            .That(result)
            .Contains("global::TestApp.Controllers.StaticController.GetPage();");
        await Assert
            .That(result)
            .DoesNotContain("(global::TestApp.Controllers.StaticController)__controller");
        await Assert
            .That(result)
            .DoesNotContain("GetService(typeof(global::TestApp.Controllers.StaticController))");
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

        // Strings bind raw — nothing can fail to parse, so no 404 guard may
        // appear; the only guard is the 400 for a missing route/query value.
        await Assert.That(result).Contains("out var __id)");
        await Assert.That(result).Contains("PicoWeb.Results.Empty(400)");
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

    // ─────────────────────── diagnostics (task T5.7, audit F-55) ───────────────────────

    /// <summary>
    /// A controller that is <em>discovered</em> and then maps nothing used to disappear without a
    /// trace: the transform returned <c>null</c>, which is indistinguishable from "this class is
    /// not a controller" — the route 404s at run time and the build says nothing.
    /// <para>
    /// <b>This is PCW001's test.</b> It asserts the three things that make a diagnostic worth
    /// anything: the id, the severity, and a <see cref="Location"/> that points at the
    /// controller's identifier. A diagnostic without one cannot be navigated to, so it exists for
    /// the log only.
    /// </para>
    /// </summary>
    [Test]
    public async Task PCW001_warns_at_a_controller_that_maps_no_action()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public string Help() => "help";
            }
            """;

        var run = GeneratorHarness.RunGeneratorRun(source, "Controllers/UsersController.cs");

        var diagnostics = run.GeneratorDiagnostics.Where(static d => d.Id == "PCW001").ToArray();
        await Assert.That(diagnostics.Length).IsEqualTo(1);

        var diagnostic = diagnostics[0];
        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        // `location: null` — what PWR001 used to report — is the defect; nothing here may repeat it.
        await Assert.That(diagnostic.Location.Kind).IsNotEqualTo(LocationKind.None);
        await Assert
            .That(diagnostic.Location.GetLineSpan().Path)
            .IsEqualTo("Controllers/UsersController.cs");
        // …and it points at the identifier, not merely at the file.
        await Assert.That(GeneratorHarness.TextAt(diagnostic, source)).IsEqualTo("UsersController");
        await Assert.That(diagnostic.GetMessage()).Contains("UsersController");

        // The controller is still not mapped: the diagnostic explains the silence, it does not
        // invent an endpoint for an action that answers no verb.
        await Assert.That(run.Generated).DoesNotContain("app.MapGet");
    }

    /// <summary>
    /// A generic controller cannot be mapped (audit F-53's generic half): the emitted
    /// <c>global::Ns.ItemsController&lt;T&gt;</c> left <c>T</c> unbound and the project failed
    /// with CS0246. T5.5 skipped it and left a comments-only note in
    /// <c>SkippedControllers.g.cs</c>; <b>this diagnostic supersedes that note</b> — a comment in
    /// a generated file is invisible unless you go looking for it in <c>obj/</c>, while an error
    /// at the controller's own identifier is not.
    /// </summary>
    [Test]
    public async Task PCW002_errors_at_a_generic_controller()
    {
        var source = """
            using PicoNode.Web;
            namespace MyApp.Controllers;
            public class ItemsController<T>
            {
                [HttpGet("items")]
                public string Get() => "items";
            }
            """;

        var run = GeneratorHarness.RunGeneratorRun(source, "Controllers/ItemsController.cs");

        var diagnostics = run.GeneratorDiagnostics.Where(static d => d.Id == "PCW002").ToArray();
        await Assert.That(diagnostics.Length).IsEqualTo(1);

        var diagnostic = diagnostics[0];
        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Location.Kind).IsNotEqualTo(LocationKind.None);
        await Assert
            .That(diagnostic.Location.GetLineSpan().Path)
            .IsEqualTo("Controllers/ItemsController.cs");
        await Assert.That(GeneratorHarness.TextAt(diagnostic, source)).IsEqualTo("ItemsController");
        await Assert
            .That(diagnostic.GetMessage())
            .Contains("global::MyApp.Controllers.ItemsController<T>");

        // The skip itself is unchanged: no endpoint, no DI registration, and no note file.
        await Assert
            .That(run.Generated)
            .DoesNotContain("typeof(global::MyApp.Controllers.ItemsController");
        await Assert.That(run.Generated).DoesNotContain("Controllers.Gen skipped");
    }

    /// <summary>
    /// An action that returns a DTO is emitted as
    /// <c>PicoJetson.JsonSerializer.SerializeToUtf8Bytes(&lt;dto&gt;)</c>, so the DTO needs a
    /// generated serialiser, which PicoJetson only emits for a type marked
    /// <c>[PicoJsonSerializable]</c>. Without the marker the endpoint still compiles and throws at
    /// request time — the worst place to learn about it.
    /// </summary>
    [Test]
    public async Task PCW003_warns_at_a_DTO_that_is_not_marked_PicoJsonSerializable()
    {
        var source = """
            namespace MyApp.Controllers;
            public class UsersController
            {
                public UserDto GetUser(int id) => new UserDto();
            }
            public class UserDto
            {
                public string Name { get; set; } = "";
            }
            """;

        var run = GeneratorHarness.RunGeneratorRun(source, "Controllers/UsersController.cs");

        var diagnostics = run.GeneratorDiagnostics.Where(static d => d.Id == "PCW003").ToArray();
        await Assert.That(diagnostics.Length).IsEqualTo(1);

        var diagnostic = diagnostics[0];
        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Location.Kind).IsNotEqualTo(LocationKind.None);
        // The DTO's own declaration is the only useful location for this one: that is where the
        // attribute has to be added.
        await Assert.That(GeneratorHarness.TextAt(diagnostic, source)).IsEqualTo("UserDto");
        await Assert.That(diagnostic.GetMessage()).Contains("UserDto");

        // The endpoint is still mapped: this is a serialisation trap, not a mapping failure.
        await Assert.That(run.Generated).Contains("SerializeToUtf8Bytes");
    }

    /// <summary>
    /// The other half of PCW003, and the half that decides whether the rule is usable: it must
    /// stay silent for everything that is <em>not</em> a source-declared DTO in a serialised
    /// return position — a marked DTO, an <c>IWebResult</c> (executed, never serialised) and a
    /// framework type. A warning that fires on those is noise, and noise gets suppressed.
    /// </summary>
    [Test]
    public async Task PCW003_stays_silent_for_marked_DTOs_and_for_non_serialised_returns()
    {
        var source = """
            using PicoJetson;
            using PicoNode.Web;
            namespace MyApp.Controllers;
            public class MixedController
            {
                public MarkerDto GetMarked(int id) => new MarkerDto();
                public HtmlResult GetPage() => new HtmlResult("ok");
                public string GetName() => "name";
                public System.Collections.Generic.List<MarkerDto> GetAll() => new();
            }
            [PicoJsonSerializable]
            public class MarkerDto
            {
                public string Name { get; set; } = "";
            }
            """;

        var run = GeneratorHarness.RunGeneratorRun(source, "Controllers/MixedController.cs");

        await Assert
            .That(
                string.Join(
                    " | ",
                    run.GeneratorDiagnostics.Select(static d => $"{d.Id} {d.GetMessage()}")
                )
            )
            .IsEmpty();
    }

    // ─────────────────────────── routes (task T5.7) ───────────────────────────

    /// <summary>
    /// <c>[Route("api/x")]</c> — no leading slash — was taken verbatim as the route prefix, so
    /// the generated <c>app.MapGet("api/x/…")</c> throws
    /// <c>ArgumentException("Route patterns must start with '/'.")</c> inside
    /// <c>WebApp.Build()</c>: the app dies at startup, naming none of the user's code.
    /// <para>
    /// Registering the emitted route on a <em>real</em> router is the assertion, on purpose —
    /// <c>Build()</c> throwing <em>is</em> the defect, and a substring assertion would have been
    /// satisfied by any number of wrong routes.
    /// </para>
    /// </summary>
    [Test]
    public async Task Route_prefix_without_a_leading_slash_cannot_throw_at_startup()
    {
        var source = """
            using PicoNode.Web;
            namespace MyApp.Controllers;
            [Route("api/x")]
            public class ItemsController
            {
                [HttpGet("list")]
                public string GetList() => "items";
            }
            """;

        var run = GeneratorHarness.RunGeneratorRun(source, "Controllers/ItemsController.cs");

        var route = FirstMapRoute(run.Generated);
        var app = new WebApp(new SpyContainer());
        app.MapGet(route, static (_, _) => ValueTask.FromResult(PicoWeb.Results.Empty(204)));
        app.Build();

        await Assert.That(route).IsEqualTo("/api/x/list");
        await Assert.That(run.Generated).Contains("MapGet(\"/api/x/list\"");
    }

    /// <summary>
    /// A verb attribute's route used to be read out of the <em>syntax</em>
    /// (<c>arg.ToString().Trim('"')</c>), so <c>[HttpGet(Routes.Posts)]</c> emitted the literal
    /// route <c>Routes.Posts</c> — a path no request can match, with a clean build. The
    /// attribute's <see cref="AttributeData.ConstructorArguments"/> carry the constant's value,
    /// which is the string the user wrote.
    /// </summary>
    [Test]
    public async Task Method_route_is_the_constant_value_not_the_expression_text()
    {
        var source = """
            using PicoNode.Web;
            namespace MyApp.Controllers;
            public static class Routes
            {
                public const string Posts = "posts";
            }
            public class PostsController
            {
                [HttpGet(Routes.Posts)]
                public string Get() => "posts";
            }
            """;

        var run = GeneratorHarness.RunGeneratorRun(source, "Controllers/PostsController.cs");

        await Assert.That(run.Generated).Contains("MapGet(\"/api/posts/posts\"");
        await Assert.That(run.Generated).DoesNotContain("Routes.Posts");
    }

    // ─────────────────────────── incrementality (task T5.6) ───────────────────────

    /// <summary>
    /// "Incremental" is not visible in the generated text: a generator can emit exactly the
    /// same output every time and still re-run everything on every keystroke. It is visible in
    /// the driver's <em>step reasons</em>, so that is what is asserted here — run twice, change
    /// a file no controller lives in, and the model step (the transform's output, tracked as
    /// <c>Controllers</c>) must come back <c>Cached</c>/<c>Unchanged</c>.
    /// <para>
    /// Before T5.6 this test was red: <c>ControllerModel</c> held
    /// <c>ISymbol</c> values and <c>MethodModel</c> held an <c>IMethodSymbol</c>, none of them
    /// comparable, so every run produced a "different" model and everything below it re-ran —
    /// the text was rebuilt from scratch for a keystroke in any file of the project.
    /// </para>
    /// </summary>
    [Test]
    public async Task Generator_output_is_cached_across_an_unrelated_edit()
    {
        var controllers = Tree("Controllers/PostsController.cs", PostsControllerSource);
        var unrelated = Tree("Unrelated.cs", UnrelatedSource);
        var compilation = CompilationOf(controllers, unrelated);

        GeneratorDriver driver = CreateTrackedDriver();
        driver = driver.RunGenerators(compilation);

        // The first run really did build a model: without this, "nothing re-ran" would also be
        // satisfied by a pipeline that never ran at all.
        await Assert.That(ControllerStep(driver).Length).IsEqualTo(1);

        // A keystroke in a file that has nothing to do with any controller — the edit that used
        // to trigger a full regeneration.
        var edited = Tree("Unrelated.cs", UnrelatedSource + "\n// a comment, nothing more\n");
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(unrelated, edited));

        await Assert.That(RerunDescription(driver, "Controllers")).IsEmpty();

        // The compilation is still read — that is how the project-level questions are answered —
        // but the answer is a value, so this step is not forced either.
        await Assert.That(RerunDescription(driver, "CompilationState")).IsEmpty();

        // The strongest form: the source output itself did not re-execute, i.e. no text was
        // regenerated for this edit.
        await Assert.That(SourceOutputRerunDescription(driver)).IsEmpty();
    }

    /// <summary>
    /// The other half of the property: caching must not be so coarse that one edited controller
    /// throws away the work for all of them. Two controllers in two files; edit one; exactly
    /// that one is rebuilt.
    /// </summary>
    [Test]
    public async Task Only_the_changed_controller_is_rebuilt()
    {
        var posts = Tree("Controllers/PostsController.cs", PostsControllerSource);
        var comments = Tree("Controllers/CommentsController.cs", CommentsControllerSource);
        var compilation = CompilationOf(posts, comments);

        GeneratorDriver driver = CreateTrackedDriver();
        driver = driver.RunGenerators(compilation);
        await Assert.That(ControllerStep(driver).Length).IsEqualTo(2);

        // Change one controller's route, and nothing else.
        var editedPosts = Tree(
            "Controllers/PostsController.cs",
            PostsControllerSource.Replace("\"posts\"", "\"articles\"", StringComparison.Ordinal)
        );
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(posts, editedPosts));

        var rebuilt = ControllerStep(driver)
            .Where(static o =>
                o.Reason
                    is not (IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged)
            )
            .ToArray();

        // Exactly one model was rebuilt — not both (over-invalidating), and not none (the edit
        // would have been lost). This fails as `Modified: …PostsController…` if the assertion
        // below is ever wrong about which one.
        await Assert.That(rebuilt.Length).IsEqualTo(1);
        await Assert.That(rebuilt[0].Value).Contains("PostsController");

        // …and the untouched controller's output is `Cached`: the driver did not treat it as
        // a change to propagate. (The syntax transform still re-runs — Roslyn does not
        // cache it, measured and noted in ControllersGenerator's class remarks; what value
        // equality buys is that its output is not seen as a modification.)
        await Assert
            .That(
                ControllerStep(driver)
                    .Any(static o =>
                        o.Value.Contains("CommentsController", StringComparison.Ordinal)
                        && o.Reason == IncrementalStepRunReason.Cached
                    )
            )
            .IsTrue();
    }

    private const string PostsControllerSource = """
        using PicoNode.Web;

        namespace TestApp.Controllers;

        public sealed class PostsController
        {
            [HttpGet("posts")]
            public string Get() => "posts";
        }
        """;

    private const string CommentsControllerSource = """
        using PicoNode.Web;

        namespace TestApp.Controllers;

        public sealed class CommentsController
        {
            [HttpGet("comments")]
            public string Get() => "comments";
        }
        """;

    private const string UnrelatedSource = """
        namespace TestApp;

        public sealed class Unrelated
        {
            public int Value => 1;
        }
        """;

    private static SyntaxTree Tree(string path, string source) =>
        CSharpSyntaxTree.ParseText(source, path: path);

    private static CSharpCompilation CompilationOf(params SyntaxTree[] trees) =>
        CSharpCompilation.Create(
            "IncrementalAssembly",
            trees,
            GeneratorHarness.BaseReferences,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)
        );

    /// <summary>
    /// A driver that <em>tracks step run reasons</em>, which is the only way to observe whether
    /// a generator is incremental: <c>GetRunResult().Results[0].TrackedSteps</c> stays empty
    /// unless this option is set. Same generator, same references as every other test here —
    /// only the bookkeeping is switched on.
    /// </summary>
    private static GeneratorDriver CreateTrackedDriver() =>
        CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new ControllersGenerator().AsSourceGenerator() },
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true
            )
        );

    /// <summary>
    /// Every output of the <c>Controllers</c> step as <c>(reason, the model's text)</c>. The
    /// text is what names <em>which</em> controller a reason belongs to.
    /// </summary>
    private static (IncrementalStepRunReason Reason, string Value)[] ControllerStep(
        GeneratorDriver driver
    ) => Step(driver, "Controllers");

    private static (IncrementalStepRunReason Reason, string Value)[] Step(
        GeneratorDriver driver,
        string name
    )
    {
        var tracked = driver.GetRunResult().Results[0].TrackedSteps;
        if (!tracked.TryGetValue(name, out var steps))
            throw new InvalidOperationException(
                $"the generator tracks no step named '{name}', so its cache behaviour cannot be "
                    + "observed — the pipeline must mark it with WithTrackingName"
            );

        return
        [
            .. steps
                .SelectMany(static step => step.Outputs)
                .Select(static o => (o.Reason, o.Value?.ToString() ?? "<null>")),
        ];
    }

    /// <summary>
    /// "" when every output of the named step was <c>Cached</c> or <c>Unchanged</c>, otherwise
    /// one entry per rebuilt output — so a failure names the reason and the model instead of
    /// only saying "expected empty".
    /// </summary>
    private static string RerunDescription(GeneratorDriver driver, string name) =>
        string.Join(
            " | ",
            Step(driver, name)
                .Where(static o =>
                    o.Reason
                        is not (
                            IncrementalStepRunReason.Cached
                            or IncrementalStepRunReason.Unchanged
                        )
                )
                .Select(static o => $"{o.Reason}: {o.Value}")
        );

    /// <summary>
    /// The <c>SourceOutput</c> step is where the text is generated; if it re-executes, the point
    /// of the cache is lost even when the models above it were reused.
    /// </summary>
    private static string SourceOutputRerunDescription(GeneratorDriver driver) =>
        RerunDescription(driver, "SourceOutput");

    // The generator spine lives in GeneratorHarness so that a string assertion and a
    // compile assertion run the *same* driver over the *same* reference set: they must
    // not be able to disagree about what the generator emitted.
    /// <summary>
    /// The first route the generator emitted, so a test can hand it to the real router instead of
    /// comparing it as a string. Throws when there is none: an empty route list would make the
    /// "registers without throwing" assertion vacuous.
    /// </summary>
    private static string FirstMapRoute(string generated)
    {
        var match = MapRoutePattern().Match(generated);
        if (!match.Success)
            throw new InvalidOperationException(
                "the generator emitted no Map* route, so there is nothing to register"
            );

        return match.Groups["route"].Value;
    }

    private static Regex MapRoutePattern() =>
        new(
            "app\\.Map(?:Get|Post|Put|Delete|Patch)\\(\"(?<route>[^\"]+)\"",
            RegexOptions.CultureInvariant
        );

    private static string RunGenerator(
        string source,
        string fileName,
        IReadOnlyList<MetadataReference>? extraReferences = null
    ) => GeneratorHarness.RunGenerator(source, fileName, extraReferences).Generated;

    private static (
        string Generated,
        IReadOnlyList<Diagnostic> Cs0436Diagnostics
    ) RunGeneratorWithDiagnostics(
        string source,
        string fileName,
        IReadOnlyList<MetadataReference> extraReferences
    ) => GeneratorHarness.RunGenerator(source, fileName, extraReferences);

    /// <summary>
    /// Builds a referenced assembly that already exports EndpointRegistrar — the
    /// "Exe B references Exe A (with controllers)" shape. Shared with
    /// <see cref="GeneratorHarness"/> so the CS0436 exemption is exercised by the same
    /// reference the string assertions use.
    /// </summary>
    private static MetadataReference CreateAssemblyWithEndpointRegistrar() =>
        GeneratorHarness.ImportedRegistrarReference();
}
