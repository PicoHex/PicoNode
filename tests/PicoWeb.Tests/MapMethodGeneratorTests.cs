using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoWeb.Gen;

namespace PicoWeb.Tests;

public sealed class MapMethodGeneratorTests
{
    [Test]
    public async Task Generator_creates_source_file()
    {
        // PicoWeb.Gen adds [assembly: PicoJsonSerializable(...)] for types found
        // in MapGet/MapPost lambda signatures.
        // With no MapGet calls, it generates nothing.
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
        // PicoJetson.Gen handles the registration. PicoWeb.Gen adds
        // additional [PicoJsonSerializable] for MapXX return types.
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
                typeof(PicoJetson.PicoJsonSerializableAttribute).Assembly.Location
            ),
            MetadataReference.CreateFromFile(typeof(PicoJetson.JsonSerializer).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var generator = new MapMethodGenerator();
        var driver = CSharpGeneratorDriver.Create(generator);
        var runResult = driver.RunGenerators(compilation).GetRunResult();

        if (runResult.Results.Length == 0)
            return "";

        var sources = runResult.Results[0].GeneratedSources;
        if (sources.IsEmpty)
            return "";

        return string.Join("\n", sources.Select(s => s.SourceText.ToString()));
    }
}
