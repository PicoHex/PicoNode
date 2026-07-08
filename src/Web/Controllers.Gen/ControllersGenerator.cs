namespace Controllers.Gen;

[Generator(LanguageNames.CSharp)]
public sealed class ControllersGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var controllerClasses = context
            .SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => GetClassModel(ctx, ct)
            )
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        var combined = controllerClasses.Collect();

        context.RegisterSourceOutput(combined, GenerateSources);
    }

    private static ControllerModel? GetClassModel(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        // Only generate for executable projects that reference PicoNode.Web.WebApp
        var comp = ctx.SemanticModel.Compilation;
        if (comp.GetTypeByMetadataName("PicoNode.Web.WebApp") is null)
            return null;
        // Library projects (DLL) would export EndpointRegistrar and cause conflicts;
        // only ConsoleApplication / WindowsApplication need generated endpoints.
        var kind = comp.Options.OutputKind;
        if (kind != OutputKind.ConsoleApplication && kind != OutputKind.WindowsApplication)
            return null;

        var classDecl = (ClassDeclarationSyntax)ctx.Node;
        var filePath = classDecl.SyntaxTree.FilePath;

        // Match Controllers/ folder as a directory segment
        var normalized = filePath.Replace('\\', '/');
        var inControllersFolder =
            normalized.IndexOf("/Controllers/", StringComparison.Ordinal) >= 0
            || normalized.StartsWith("Controllers/", StringComparison.Ordinal);

        var semanticModel = ctx.SemanticModel;
        var classSymbol = semanticModel.GetDeclaredSymbol(classDecl, ct) as INamedTypeSymbol;
        if (classSymbol is null)
            return null;

        // Also accept classes decorated with [ApiController] (or custom [ApiControllerAttribute])
        // regardless of their folder location
        var hasApiControllerAttr = classSymbol
            .GetAttributes()
            .Any(a => a.AttributeClass?.Name == "ApiControllerAttribute");

        if (!inControllersFolder && !hasApiControllerAttr)
            return null;
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

            var bodyText = member.Body?.ToString() ?? "";
            methods.Add(new MethodModel(httpMethod, route, methodSymbol, bodyText));
        }

        if (methods.Count == 0)
            return null;

        var controllerName = classSymbol.Name;
        var controllerFullName = classSymbol.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat
        );
        var routePrefix = GetRoutePrefix(classDecl, classSymbol);

        return new ControllerModel(controllerName, controllerFullName, routePrefix, methods);
    }

    private static string? GetHttpMethod(MethodDeclarationSyntax method, IMethodSymbol methodSymbol)
    {
        // Check for [HttpGet], [HttpPost], etc. attributes
        foreach (var attr in methodSymbol.GetAttributes())
        {
            var name = attr.AttributeClass?.Name;
            switch (name)
            {
                case "HttpGetAttribute":
                    return "GET";
                case "HttpPostAttribute":
                    return "POST";
                case "HttpPutAttribute":
                    return "PUT";
                case "HttpDeleteAttribute":
                    return "DELETE";
                case "HttpPatchAttribute":
                    return "PATCH";
            }
        }

        // Convention: method name prefix
        var methodName = methodSymbol.Name;
        if (methodName.StartsWith("Get"))
            return "GET";
        if (methodName.StartsWith("Post"))
            return "POST";
        if (methodName.StartsWith("Put"))
            return "PUT";
        if (methodName.StartsWith("Delete"))
            return "DELETE";
        if (methodName.StartsWith("Patch"))
            return "PATCH";

        return null;
    }

    private static string GetRoute(MethodDeclarationSyntax method, IMethodSymbol methodSymbol)
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

        // Convert method name: GetUserById → /user/{id}
        var routeParams = new List<string>();
        foreach (var param in methodSymbol.Parameters)
        {
            // Simple types (int, string, Guid, etc.) become route parameters
            if (!IsComplexType(param.Type))
                routeParams.Add("{" + param.Name + "}");
        }

        // Strip trailing parameter name from method name if present
        var lastParam = methodSymbol.Parameters.LastOrDefault();
        if (
            lastParam != null
            && methodName.IndexOf(lastParam.Name, StringComparison.OrdinalIgnoreCase) >= 0
        )
        {
            methodName = methodName.Substring(0, methodName.Length - lastParam.Name.Length);
        }

        // If method name is empty after stripping prefix, omit the method path segment
        var route = string.IsNullOrEmpty(methodName) ? "" : "/" + ToKebabCase(methodName);
        if (routeParams.Count > 0)
            route += "/" + string.Join("/", routeParams);
        return route;
    }

    private static string GetRoutePrefix(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol
    )
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

    private static void GenerateSources(
        SourceProductionContext context,
        System.Collections.Immutable.ImmutableArray<ControllerModel> controllers
    )
    {
        var registrarCode = new StringBuilder();
        registrarCode.AppendLine("#nullable enable");

        if (controllers.Length == 0)
        {
            // No controllers — use object parameter so this compiles even in projects
            // without PicoNode.Web reference (e.g., class libraries using the analyzer).
            registrarCode.AppendLine("public static class EndpointRegistrar");
            registrarCode.AppendLine("{");
            registrarCode.AppendLine("    public static void RegisterAll(object app)");
            registrarCode.AppendLine("    {");
            registrarCode.AppendLine("        // No controllers found — nothing to register");
            registrarCode.AppendLine("    }");
            registrarCode.AppendLine("}");
            context.AddSource("EndpointRegistrar.g.cs", registrarCode.ToString());
            return;
        }

        // Controllers exist — strongly-typed WebApp parameter
        registrarCode.AppendLine("using PicoNode.Web;");
        registrarCode.AppendLine();
        registrarCode.AppendLine("public static class EndpointRegistrar");
        registrarCode.AppendLine("{");
        registrarCode.AppendLine("    public static void RegisterAll(WebApp app)");
        registrarCode.AppendLine("    {");

        var endpointsCode = new StringBuilder();
        endpointsCode.AppendLine("// <auto-generated/>");
        endpointsCode.AppendLine("#nullable enable");
        var registrarMethods = new StringBuilder();

        foreach (var controller in controllers)
        {
            var stubClassName = controller.Name + "_Endpoints";

            endpointsCode.AppendLine($"    public static class {stubClassName}");
            endpointsCode.AppendLine("    {");
            endpointsCode.AppendLine(
                $"        public static void Register(PicoNode.Web.WebApp app)"
            );
            endpointsCode.AppendLine("        {");

            foreach (var method in controller.Methods)
            {
                var mapMethod = method.HttpMethod switch
                {
                    "GET" => "MapGet",
                    "POST" => "MapPost",
                    "PUT" => "MapPut",
                    "DELETE" => "MapDelete",
                    "PATCH" => "MapPatch",
                    _ => "MapGet",
                };

                // Ensure proper separator between prefix and method route
                var methodRoute = method.Route;
                var fullRoute = methodRoute.StartsWith("/")
                    ? controller.RoutePrefix + methodRoute
                    : controller.RoutePrefix + "/" + methodRoute;

                // Analyze return type before emitting the lambda so we know
                // whether to add 'async'.
                var retType = method.Symbol.ReturnType;
                var isVoid = retType.SpecialType == SpecialType.System_Void;
                var isAsync = false;
                var isAsyncVoid = false;
                var resultTypeName = retType.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat
                );

                if (retType is INamedTypeSymbol namedRet)
                {
                    var name = namedRet.Name;
                    if (name is "Task" or "ValueTask")
                    {
                        isAsync = true;
                        if (namedRet.TypeArguments.Length == 1)
                        {
                            resultTypeName = namedRet
                                .TypeArguments[0]
                                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        }
                        else
                        {
                            isAsyncVoid = true;
                        }
                    }
                }

                var asyncPrefix = isAsync ? "async " : "";
                endpointsCode.AppendLine(
                    $"            app.{mapMethod}(\"{fullRoute}\", {asyncPrefix}(WebContext ctx, CancellationToken _) =>"
                );
                endpointsCode.AppendLine("            {");

                // Bind route parameters and DI services
                var callArgs = new List<string>();
                foreach (var param in method.Symbol.Parameters)
                {
                    var typeName = param.Type.ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat
                    );
                    var paramName = param.Name;

                    if (!IsComplexType(param.Type))
                    {
                        var parseExpr = GetRouteValueParseExpression(typeName, paramName);
                        endpointsCode.AppendLine(
                            $"                var __{paramName} = {parseExpr};"
                        );
                    }
                    else
                    {
                        // Complex type — resolve from DI
                        endpointsCode.AppendLine(
                            $"                var __{paramName} = ctx.Services.GetService(typeof({typeName}));"
                        );
                    }
                    callArgs.Add($"__{paramName}");
                }

                // Collect and resolve the controller
                endpointsCode.AppendLine(
                    $"                var __controller = ctx.Services.GetService(typeof({controller.FullName}))!;"
                );

                var awaitPrefix = isAsync ? "await " : "";

                if (isVoid || isAsyncVoid)
                {
                    endpointsCode.AppendLine(
                        $"                {awaitPrefix}(({controller.FullName})__controller).{method.Symbol.Name}({string.Join(", ", callArgs)});"
                    );
                    if (isAsync)
                        endpointsCode.AppendLine("                return PicoWeb.Results.Empty(204);");
                    else
                        endpointsCode.AppendLine(
                            "                return ValueTask.FromResult(PicoWeb.Results.Empty(204));"
                        );
                }
                else
                {
                    endpointsCode.AppendLine(
                        $"                var __result = ({resultTypeName}){awaitPrefix}(({controller.FullName})__controller).{method.Symbol.Name}({string.Join(", ", callArgs)});"
                    );
                    endpointsCode.AppendLine(
                        "                var __bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(__result);"
                    );
                    if (isAsync)
                        endpointsCode.AppendLine(
                            "                return PicoWeb.Results.Json(200, __bytes);"
                        );
                    else
                        endpointsCode.AppendLine(
                            "                return ValueTask.FromResult(PicoWeb.Results.Json(200, __bytes));"
                        );
                }

                endpointsCode.AppendLine("            });");
                endpointsCode.AppendLine();
            }

            endpointsCode.AppendLine("        }");
            endpointsCode.AppendLine("    }");
            endpointsCode.AppendLine();

            registrarMethods.AppendLine($"        {stubClassName}.Register(app);");
        }

        // Append registrar methods from controllers to the EndpointRegistrar
        registrarCode.Append(registrarMethods);
        registrarCode.AppendLine("    }");
        registrarCode.AppendLine("}");

        // Add sources
        if (endpointsCode.Length > 0)
        {
            var endpointSource =
                "using PicoNode.Web;\nusing PicoJetson;\n\n" + endpointsCode.ToString();
            context.AddSource("Controllers_Endpoints.g.cs", endpointSource);
        }

        context.AddSource("EndpointRegistrar.g.cs", registrarCode.ToString());

        // ── Generate DI registration for discovered controllers ──
        var diCode = new StringBuilder();
        diCode.AppendLine("// <auto-generated/>");
        diCode.AppendLine("#nullable enable");
        diCode.AppendLine("using PicoDI;");
        diCode.AppendLine("using PicoDI.Abs;");
        diCode.AppendLine();
        diCode.AppendLine("public static class ControllerServiceRegistrations");
        diCode.AppendLine("{");
        diCode.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        diCode.AppendLine("    internal static void AutoRegisterControllerServices()");
        diCode.AppendLine("    {");
        diCode.AppendLine(
            "        global::PicoDI.SvcContainerAutoConfiguration.RegisterConfigurator("
        );
        diCode.AppendLine("            \"generated::Controllers.Gen::ControllerServices\",");
        diCode.AppendLine("            static container => RegisterAll(container));");
        diCode.AppendLine("    }");
        diCode.AppendLine();
        diCode.AppendLine(
            "    private static void RegisterAll(global::PicoDI.Abs.ISvcContainer container)"
        );
        diCode.AppendLine("    {");

        foreach (var controller in controllers)
        {
            var fqn = controller.FullName; // e.g. "global::MyApp.Controllers.UsersController"
            diCode.AppendLine(
                "        container.Register(global::PicoDI.Abs.SvcDescriptor.Create("
            );
            diCode.AppendLine($"            typeof({fqn}),");
            diCode.AppendLine($"            static _ => new {fqn}(),");
            diCode.AppendLine("            global::PicoDI.Abs.SvcLifetime.Scoped));");
            diCode.AppendLine();
        }

        diCode.AppendLine("    }");
        diCode.AppendLine("}");

        context.AddSource("ControllerServiceRegistrations.g.cs", diCode.ToString());
    }

    private static string GetRouteValueParseExpression(string typeName, string paramName)
    {
        var key = $"ctx.RouteValues[\"{paramName}\"]";
        var inv = "System.Globalization.CultureInfo.InvariantCulture";

        return typeName switch
        {
            "int" or "global::System.Int32" => $"int.Parse({key}, {inv})",
            "long" or "global::System.Int64" => $"long.Parse({key}, {inv})",
            "short" or "global::System.Int16" => $"short.Parse({key}, {inv})",
            "ushort" or "global::System.UInt16" => $"ushort.Parse({key}, {inv})",
            "uint" or "global::System.UInt32" => $"uint.Parse({key}, {inv})",
            "ulong" or "global::System.UInt64" => $"ulong.Parse({key}, {inv})",
            "byte" or "global::System.Byte" => $"byte.Parse({key}, {inv})",
            "sbyte" or "global::System.SByte" => $"sbyte.Parse({key}, {inv})",
            "float" or "global::System.Single" => $"float.Parse({key}, {inv})",
            "double" or "global::System.Double" => $"double.Parse({key}, {inv})",
            "decimal" or "global::System.Decimal" => $"decimal.Parse({key}, {inv})",
            "bool" or "global::System.Boolean" => $"bool.Parse({key})",
            "char" or "global::System.Char" => $"char.Parse({key})",
            "global::System.Guid" => $"global::System.Guid.Parse({key})",
            "global::System.DateTime" => $"global::System.DateTime.Parse({key}, {inv})",
            "global::System.DateTimeOffset" => $"global::System.DateTimeOffset.Parse({key}, {inv})",
            "global::System.TimeSpan" => $"global::System.TimeSpan.Parse({key}, {inv})",
            "global::System.DateOnly" => $"global::System.DateOnly.Parse({key}, {inv})",
            "global::System.TimeOnly" => $"global::System.TimeOnly.Parse({key}, {inv})",
            "string" or "global::System.String" => key,
            _ => $"({typeName})System.Enum.Parse(typeof({typeName}), {key})",
        };
    }

    private static bool IsComplexType(ITypeSymbol type)
    {
        if (type.SpecialType != SpecialType.None)
            return false;
        if (type.TypeKind == TypeKind.Enum)
            return false;
        var name = type.ToDisplayString();
        var fqName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (
            name
                is "string"
                    or "bool"
                    or "int"
                    or "long"
                    or "double"
                    or "float"
                    or "decimal"
                    or "char"
                    or "byte"
                    or "short"
                    or "uint"
                    or "ulong"
                    or "ushort"
                    or "sbyte"
                    or "Guid"
                    or "DateTime"
                    or "DateTimeOffset"
                    or "DateOnly"
                    or "TimeOnly"
                    or "TimeSpan"
            || fqName
                is "global::System.Guid"
                    or "global::System.DateTime"
                    or "global::System.DateTimeOffset"
                    or "global::System.DateOnly"
                    or "global::System.TimeOnly"
                    or "global::System.TimeSpan"
        )
            return false;
        return true;
    }
}

internal sealed class ControllerModel
{
    public string Name { get; }
    public string FullName { get; }
    public string RoutePrefix { get; }
    public List<MethodModel> Methods { get; }

    public ControllerModel(
        string name,
        string fullName,
        string routePrefix,
        List<MethodModel> methods
    )
    {
        Name = name;
        FullName = fullName;
        RoutePrefix = routePrefix;
        Methods = methods;
    }
}

internal sealed class MethodModel
{
    public string HttpMethod { get; }
    public string Route { get; }
    public IMethodSymbol Symbol { get; }
    public string BodyText { get; }

    public MethodModel(string httpMethod, string route, IMethodSymbol symbol, string bodyText)
    {
        HttpMethod = httpMethod;
        Route = route;
        Symbol = symbol;
        BodyText = bodyText;
    }
}
