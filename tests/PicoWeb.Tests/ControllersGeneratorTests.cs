using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Controllers.Gen;

namespace PicoWeb.Tests;

public sealed class ControllersGeneratorTests
{
    [Test]
    public async Task Controller_in_Controllers_folder_generates_output()
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
        await Assert.That(result).Contains("EndpointRegistrar");
    }

    [Test]
    public async Task Controller_with_DTO_return_generates_serializable()
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

        await Assert.That(result).Contains("[assembly: PicoJetson.PicoJsonSerializable(typeof(global::MyApp.Controllers.UserDto))]");
    }

    [Test]
    public async Task File_outside_Controllers_folder_is_ignored()
    {
        var source = """
            namespace MyApp;
            public class NotAController
            {
                public string GetSomething() { return "ok"; }
            }
            """;

        var result = RunGenerator(source, "Models/NotAController.cs");

        await Assert.That(result.Length).IsEqualTo(0);
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
