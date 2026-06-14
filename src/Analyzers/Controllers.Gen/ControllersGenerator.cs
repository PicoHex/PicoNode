using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Controllers.Gen;

[Generator(LanguageNames.CSharp)]
public sealed class ControllersGenerator : IIncrementalGenerator
{
    private const string ControllersFolder = "Controllers";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var controllerClasses = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => node is ClassDeclarationSyntax,
            transform: static (ctx, ct) => GetClassModel(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        var combined = controllerClasses.Collect();

        context.RegisterSourceOutput(combined, GenerateSources);
    }

    private static ControllerModel? GetClassModel(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)ctx.Node;
        var filePath = classDecl.SyntaxTree.FilePath;

        if (!filePath.Contains(ControllersFolder))
            return null;

        var semanticModel = ctx.SemanticModel;
        var classSymbol = semanticModel.GetDeclaredSymbol(classDecl, ct) as INamedTypeSymbol;
        if (classSymbol is null)
            return null;

        var methods = new List<MethodModel>();
        foreach (var member in classDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            ct.ThrowIfCancellationRequested();

            var methodSymbol = semanticModel.GetDeclaredSymbol(member, ct) as IMethodSymbol;
            if (methodSymbol is null || methodSymbol.MethodKind != MethodKind.Ordinary)
                continue;

            var httpMethod = GetHttpMethod(member, methodSymbol);
            if (httpMethod is null)
                continue;

            var route = GetRoute(member, methodSymbol);

            methods.Add(new MethodModel(httpMethod, route, methodSymbol));
        }

        if (methods.Count == 0)
            return null;

        var controllerName = classSymbol.Name;
        var routePrefix = GetRoutePrefix(classDecl, classSymbol);

        return new ControllerModel(controllerName, routePrefix, methods);
    }

    private static string? GetHttpMethod(MethodDeclarationSyntax method, IMethodSymbol methodSymbol)
    {
        // Check for [HttpGet], [HttpPost], etc. attributes
        foreach (var attr in methodSymbol.GetAttributes())
        {
            var name = attr.AttributeClass?.Name;
            switch (name)
            {
                case "HttpGetAttribute": return "GET";
                case "HttpPostAttribute": return "POST";
                case "HttpPutAttribute": return "PUT";
                case "HttpDeleteAttribute": return "DELETE";
                case "HttpPatchAttribute": return "PATCH";
            }
        }

        // Convention: method name prefix
        var methodName = methodSymbol.Name;
        if (methodName.StartsWith("Get")) return "GET";
        if (methodName.StartsWith("Post")) return "POST";
        if (methodName.StartsWith("Put")) return "PUT";
        if (methodName.StartsWith("Delete")) return "DELETE";
        if (methodName.StartsWith("Patch")) return "PATCH";

        return null;
    }

    private static string GetRoute(
        MethodDeclarationSyntax method,
        IMethodSymbol methodSymbol)
    {
        // Check for [HttpGet("{id}")] attribute override
        foreach (var attrList in method.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var attrName = attr.Name.ToString();
                if (attrName is "HttpGet" or "HttpPost" or "HttpPut" or "HttpDelete" or "HttpPatch")
                {
                    if (attr.ArgumentList?.Arguments.Count == 1)
                    {
                        var arg = attr.ArgumentList.Arguments[0].ToString();
                        return arg.Trim('"');
                    }
                }
            }
        }

        // Convention: method name → path
        var methodName = methodSymbol.Name;
        foreach (var prefix in new[] { "Get", "Post", "Put", "Delete", "Patch" })
        {
            if (methodName.StartsWith(prefix))
            {
                methodName = methodName.Substring(prefix.Length);
                break;
            }
        }

        // Convert method name segments: GetUserById → /user/by/{id}
        var parts = new List<string>();
        var routeParams = new List<string>();
        foreach (var param in methodSymbol.Parameters)
        {
            routeParams.Add(param.Name);
        }

        var lastParam = routeParams.LastOrDefault();
        if (lastParam != null && methodName.IndexOf(lastParam, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            methodName = methodName.Substring(0, methodName.Length - lastParam.Length);
        }

        return "/" + ToKebabCase(methodName);
    }

    private static string GetRoutePrefix(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol)
    {
        // Check for [Route("api/v2/users")] attribute
        foreach (var attr in classSymbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name == "RouteAttribute")
            {
                var arg = attr.ConstructorArguments.FirstOrDefault();
                if (arg.Value is string route)
                    return route;
            }
        }

        // Convention: UsersController → /api/users
        var name = classSymbol.Name;
        if (name.EndsWith("Controller"))
            name = name.Substring(0, name.Length - "Controller".Length);

        return "/api/" + ToKebabCase(name);
    }

    private static string ToKebabCase(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var sb = new StringBuilder();
        for (var i = 0; i < input.Length; i++)
        {
            if (char.IsUpper(input[i]))
            {
                if (i > 0)
                    sb.Append('-');
                sb.Append(char.ToLowerInvariant(input[i]));
            }
            else
            {
                sb.Append(input[i]);
            }
        }
        return sb.ToString();
    }

    private static void GenerateSources(SourceProductionContext context, System.Collections.Immutable.ImmutableArray<ControllerModel> controllers)
    {
        if (controllers.Length == 0)
            return;

        var serializables = new HashSet<string>();
        var endpointsCode = new StringBuilder();
        var registrarMethods = new StringBuilder();

        foreach (var controller in controllers)
        {
            var stubClassName = controller.Name + "_Endpoints";

            endpointsCode.AppendLine($"    public static class {stubClassName}");
            endpointsCode.AppendLine("    {");
            endpointsCode.AppendLine($"        public static void Register(PicoNode.Web.WebApp app)");
            endpointsCode.AppendLine("        {");

            foreach (var method in controller.Methods)
            {
                var returnType = method.Symbol.ReturnType;
                CollectTypes(returnType, serializables);

                foreach (var param in method.Symbol.Parameters)
                {
                    if (IsComplexType(param.Type))
                        CollectTypes(param.Type, serializables);
                }

                var routeParams = string.Join(", ", method.Symbol.Parameters.Select(p =>
                {
                    var typeName = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    return $"{typeName} {p.Name}";
                }));
                if (routeParams.Length > 0)
                    routeParams = "WebContext ctx, " + routeParams;
                else
                    routeParams = "WebContext ctx";

                endpointsCode.AppendLine($"            app.Map{method.HttpMethod}(\"{controller.RoutePrefix}{method.Route}\", async ({routeParams}) =>");
                endpointsCode.AppendLine("            {");

                // Inject service resolution calls for non-primitive, non-WebContext params
                foreach (var param in method.Symbol.Parameters)
                {
                    var typeName = param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (param.Type.TypeKind == TypeKind.Class && param.Type.Name != "String")
                    {
                        endpointsCode.AppendLine($"                var {param.Name} = ({typeName})ctx.Services.GetService(typeof({typeName}))!;");
                    }
                }

                // Copy the method body — we can't access the body from IMethodSymbol directly
                // So we generate a placeholder call pattern
                var callArgs = string.Join(", ", method.Symbol.Parameters.Select(p => p.Name));
                endpointsCode.AppendLine($"                var __result = default({returnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)});");
                endpointsCode.AppendLine($"                var __bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(__result);");
                endpointsCode.AppendLine($"                return PicoWeb.Results.Json(200, __bytes);");
                endpointsCode.AppendLine("            });");
                endpointsCode.AppendLine();
            }

            endpointsCode.AppendLine("        }");
            endpointsCode.AppendLine("    }");
            endpointsCode.AppendLine();

            registrarMethods.AppendLine($"        {stubClassName}.Register(app);");
        }

        // Generate [assembly: PicoJsonSerializable(...)]
        var serializablesCode = new StringBuilder();
        foreach (var type in serializables)
        {
            serializablesCode.AppendLine($"[assembly: PicoJetson.PicoJsonSerializable(typeof({type}))]");
        }

        // Generate EndpointRegistrar
        var registrarCode = new StringBuilder();
        registrarCode.AppendLine("using PicoNode.Web;");
        registrarCode.AppendLine();
        registrarCode.AppendLine("public static class EndpointRegistrar");
        registrarCode.AppendLine("{");
        registrarCode.AppendLine("    public static void RegisterAll(WebApp app)");
        registrarCode.AppendLine("    {");
        registrarCode.Append(registrarMethods);
        registrarCode.AppendLine("    }");
        registrarCode.AppendLine("}");

        // Add sources
        if (serializablesCode.Length > 0)
            context.AddSource("PicoJetson_Serializables.g.cs", serializablesCode.ToString());

        if (endpointsCode.Length > 0)
        {
            var endpointSource = "using PicoNode.Web;\nusing PicoJetson;\n\n" + endpointsCode.ToString();
            context.AddSource("Controllers_Endpoints.g.cs", endpointSource);
        }

        context.AddSource("EndpointRegistrar.g.cs", registrarCode.ToString());
    }

    private static void CollectTypes(ITypeSymbol type, HashSet<string> types)
    {
        if (type is INamedTypeSymbol named)
        {
            var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (!fullName.StartsWith("global::System") && !types.Contains(fullName))
            {
                types.Add(fullName);
                foreach (var arg in named.TypeArguments)
                    CollectTypes(arg, types);
            }
        }
    }

    private static bool IsComplexType(ITypeSymbol type)
    {
        if (type.SpecialType != SpecialType.None)
            return false;
        if (type.TypeKind == TypeKind.Enum)
            return false;
        var name = type.ToDisplayString();
        if (name is "string" or "bool" or "int" or "long" or "double" or "float" or "decimal" or "Guid" or "DateTime" or "DateTimeOffset" or "DateOnly" or "TimeOnly")
            return false;
        return true;
    }
}

internal sealed class ControllerModel
{
    public string Name { get; }
    public string RoutePrefix { get; }
    public List<MethodModel> Methods { get; }

    public ControllerModel(string name, string routePrefix, List<MethodModel> methods)
    {
        Name = name;
        RoutePrefix = routePrefix;
        Methods = methods;
    }
}

internal sealed class MethodModel
{
    public string HttpMethod { get; }
    public string Route { get; }
    public IMethodSymbol Symbol { get; }

    public MethodModel(string httpMethod, string route, IMethodSymbol symbol)
    {
        HttpMethod = httpMethod;
        Route = route;
        Symbol = symbol;
    }
}
