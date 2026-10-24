namespace Controllers.Gen;

[Generator(LanguageNames.CSharp)]
public sealed class ControllersGenerator : IIncrementalGenerator
{
    /// <summary>
    /// The pipeline, and the reason it is shaped this way (task T5.6, audit F-54).
    /// <para>
    /// An incremental generator is only incremental when every step's value is
    /// <em>comparable</em>: the driver caches a step exactly when the value it produced is
    /// equal to the one from the previous run. The model used to hold
    /// <see cref="ISymbol"/> instances — <c>MethodModel.Symbol</c> — which have no value
    /// equality, so every run produced a "different" model and everything below it re-ran,
    /// including the three emitters and their several hundred lines of generated text, on every
    /// keystroke in any file of the project.
    /// </para>
    /// <para>
    /// One thing worth stating, because it is measured rather than assumed: the syntax
    /// transform is <em>not</em> cached — <c>GetClassModel</c> runs once per candidate class
    /// declaration on every pass, even when the compilation passed in is the identical
    /// instance. What value equality buys is everything <em>downstream</em>: the model compares
    /// equal, so the <c>Collect</c>/<c>Combine</c> nodes are cached and the source output is
    /// never entered, i.e. no text is regenerated.
    /// </para>
    /// <para>
    /// Two providers feed the single source output, and each carries value-only data:
    /// <c>controllerClasses</c> (a <see cref="ControllerModel"/> per controller: strings, bools
    /// and <see cref="EquatableArray{T}"/>) and <c>compilationState</c> (the project-level facts,
    /// reduced from the <see cref="Compilation"/> the moment they are read). No symbol, syntax
    /// node or compilation instance is stored in the cached part of the pipeline.
    /// </para>
    /// </summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var controllerClasses = context
            .SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => GetClassModel(ctx, ct)
            )
            .Where(static m => m is not null)
            .Select(static (m, _) => m!)
            // Named so the cache test can assert on this step's run reasons by name instead of
            // guessing at the names the driver synthesizes.
            .WithTrackingName("Controllers");

        // The project-level questions, asked as values. `hasEndpointRegistrar` has to be
        // answered even when nothing was discovered (a project with no controllers still emits
        // the `RegisterAll(object)` shim, unless an imported registrar already exists), which is
        // why it is a second provider rather than a field of some model.
        var compilationState = context
            .CompilationProvider.Select(
                static (compilation, _) =>
                    new CompilationState(
                        canHostEndpoints: CanHostEndpoints(compilation),
                        // A compilation that already has an EndpointRegistrar (typically an Exe
                        // referencing another app's generated public registrar) must not emit
                        // an empty shim: CS0436 would bind calls to the local empty
                        // implementation and the referenced app's controllers would silently
                        // never register. Projects that own controllers still emit their own
                        // registrar — an imported one cannot know about this assembly's
                        // endpoints.
                        hasEndpointRegistrar: compilation.GetTypeByMetadataName("EndpointRegistrar")
                            is not null
                    )
            )
            .WithTrackingName("CompilationState");

        context.RegisterSourceOutput(
            controllerClasses.Collect().Combine(compilationState),
            static (spc, pair) =>
            {
                var (models, state) = pair;

                // The policy is applied here, on the collected value, and not baked into the
                // models — so what the transform saw and what the emitter is allowed to emit
                // cannot drift apart.
                var controllers = state.CanHostEndpoints
                    ? models
                    : ImmutableArray<ControllerModel>.Empty;

                // Diagnostics are reported before any early return: a skipped controller's
                // diagnostic is the *reason* it produced no endpoint, so dropping it on the
                // "nothing emittable" path would silence exactly the case worth reporting. A
                // reported diagnostic declares no type, so it cannot shadow the imported
                // registrar that the early return below exists to protect.
                ReportDiagnostics(spc, controllers);

                // "No controllers" and "no controller that could be emitted" are the same
                // situation for the registrar: emitting one here would make the imported
                // registrar unreachable (CS0436 binds calls to the local, empty
                // implementation).
                if (
                    state.HasEndpointRegistrar && !controllers.Any(static c => c.SkipReason is null)
                )
                    return;

                GenerateSources(spc, controllers);
            }
        );
    }

    /// <summary>
    /// Whether this project can host generated endpoints at all: only an executable that
    /// references <c>PicoNode.Web</c>. A library would export a public
    /// <c>EndpointRegistrar</c> and collide with the app that owns the controllers.
    /// <para>
    /// Deliberately called from two places, for two different reasons, and always the same
    /// answer because it is the same method: <see cref="GetClassModel"/> asks it as a cheap
    /// pre-filter, so a library pays two dictionary lookups per class declaration and no symbol
    /// work — the cost profile it had before T5.6 — and the source output asks it as the filter
    /// that decides what may be emitted. The transform's answer cannot go stale between runs:
    /// the transform is re-evaluated on every pass (see the class remarks), so it always reads
    /// the current compilation.
    /// </para>
    /// </summary>
    private static bool CanHostEndpoints(Compilation compilation) =>
        compilation.Options.OutputKind
            is OutputKind.ConsoleApplication
                or OutputKind.WindowsApplication
        && compilation.GetTypeByMetadataName("PicoNode.Web.WebApp") is not null;

    private static ControllerModel? GetClassModel(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        // The cheap bail the pre-T5.6 transform also had, kept for the same reason: a project
        // that cannot host endpoints has nothing to describe, and this test costs two dictionary
        // lookups instead of binding a symbol for every class declaration in the project.
        if (!CanHostEndpoints(ctx.SemanticModel.Compilation))
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

        var methods = new List<MethodModel>();
        var diagnostics = new List<DiagnosticModel>();
        foreach (var member in classDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            ct.ThrowIfCancellationRequested();

            var methodSymbol = semanticModel.GetDeclaredSymbol(member, ct) as IMethodSymbol;
            if (methodSymbol is null || methodSymbol.MethodKind != MethodKind.Ordinary)
                continue;

            var httpMethod = GetHttpMethod(member, methodSymbol);
            if (httpMethod is null)
                continue;

            var route = GetRoute(methodSymbol);

            methods.Add(DescribeMethod(httpMethod, route, methodSymbol));

            // PCW003: the emitter serialises an action's non-IWebResult return value, so a DTO
            // without [PicoJsonSerializable] has no generated serializer behind that call.
            var unmarkedDto = UnmarkedSerializedType(methodSymbol);
            if (unmarkedDto is not null)
            {
                var described = DiagnosticModel.Create(
                    Diagnostics.DtoNotSerializable,
                    IdentifierLocation(unmarkedDto),
                    unmarkedDto.ToDisplayString()
                );
                if (described is not null)
                    diagnostics.Add(described);
            }
        }

        var controllerName = classSymbol.Name;
        var controllerFullName = classSymbol.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat
        );

        // A generic controller has no type argument to bind. The endpoint map used to spell
        // the type verbatim — `global::Ns.ItemsController<T>` — so every `T` was an unbound
        // type parameter and the whole project failed with CS0246 (audit F-53). Skip it, and
        // report it (PCW002): a controller the user wrote and this generator cannot map is a
        // build-time fact, and T5.5's comments-only note in `SkippedControllers.g.cs` is
        // superseded by the diagnostic — a comment in a generated file is invisible unless you
        // go looking for it, while an error at the controller's identifier is not.
        // `Arity > 0` is this type's own type parameters; `IsGenericType` additionally covers
        // an enclosing generic type (`Outer<T>.InnerController` has arity 0 and is just as
        // unrepresentable), so both are checked.
        if (classSymbol.Arity > 0 || classSymbol.IsGenericType)
        {
            return ControllerModel.Skipped(
                controllerName,
                controllerFullName,
                "generic controller: its type parameters cannot be bound in the generated "
                    + "endpoint map",
                DiagnosticModel.Create(
                    Diagnostics.UnsupportedController,
                    IdentifierLocation(classSymbol),
                    controllerFullName
                )
            );
        }

        // Discovered, and nothing to map. This used to `return null`, which is
        // indistinguishable from "this class is not a controller": the route 404s at run time
        // and the build says nothing. It is now PCW001, at the class identifier.
        //
        // Reported only for a class that says it is a controller, and that is measured rather
        // than assumed: the folder convention discovers *every* class declared in a
        // Controllers/ folder, DTOs and attribute stubs included (samples/PicoWeb.Samples
        // declares both beside its controllers), and a warning per DTO buries the one warning
        // that matters. A `FooDto` in that folder is not a controller that failed to map.
        if (methods.Count == 0)
        {
            if (
                !hasApiControllerAttr
                && !classSymbol.Name.EndsWith("Controller", StringComparison.Ordinal)
            )
                return null;

            return ControllerModel.Skipped(
                controllerName,
                controllerFullName,
                "no action matched an HTTP verb",
                DiagnosticModel.Create(
                    Diagnostics.ControllerMapsNoAction,
                    IdentifierLocation(classSymbol),
                    controllerFullName
                )
            );
        }

        var routePrefix = GetRoutePrefix(classDecl, classSymbol);

        var isStatic = classSymbol.IsStatic;
        // Prefer constructor with [SvcConstructor], otherwise first public ctor.
        var ctor = classSymbol
            .Constructors.Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .OrderByDescending(c =>
                c.GetAttributes().Any(a => a.AttributeClass?.Name == "SvcConstructorAttribute")
            )
            .FirstOrDefault();
        var constructorParameters = DescribeParameters(
            ctor?.Parameters ?? ImmutableArray<IParameterSymbol>.Empty
        );

        return new ControllerModel(
            controllerName,
            controllerFullName,
            routePrefix,
            new EquatableArray<MethodModel>(methods.ToArray()),
            isStatic,
            constructorParameters,
            new EquatableArray<DiagnosticModel>(diagnostics.ToArray())
        );
    }

    /// <summary>
    /// Reads everything the emitter needs from one action <em>here</em>, while a
    /// <see cref="SemanticModel"/> is still in hand, and returns it as a value.
    /// <para>
    /// This is the transform's whole job since T5.6: the model that travels down the
    /// pipeline holds no <see cref="IMethodSymbol"/>, so a later run over unchanged source
    /// produces a model that compares equal to the previous one and the step is cached. The
    /// discipline that keeps it honest — every emitted string is computed from the symbol in
    /// this method, never from a symbol stored for later — is why the emitted text stays
    /// byte-identical.
    /// </para>
    /// </summary>
    private static MethodModel DescribeMethod(string httpMethod, string route, IMethodSymbol symbol)
    {
        var returnType = symbol.ReturnType;
        var isWebResult = ReturnsIWebResult(returnType);
        var returnsValue = returnType.SpecialType != SpecialType.System_Void;

        // Only read when it will be emitted: for a `void`/`Task` action the result type is
        // never spelled, and carrying a meaningless string would only widen the model.
        var resultTypeName = returnsValue
            ? returnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : "";
        var isAsync = false;

        if (returnType is INamedTypeSymbol named && named.Name is "Task" or "ValueTask")
        {
            isAsync = true;
            if (named.TypeArguments.Length == 1)
            {
                resultTypeName = named
                    .TypeArguments[0]
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            }
            else
            {
                // `Task`/`ValueTask` with no result: awaited, nothing returned.
                returnsValue = false;
                resultTypeName = "";
            }
        }

        return new MethodModel(
            httpMethod,
            route,
            symbol.Name,
            symbol.IsStatic,
            isAsync,
            returnsValue,
            isWebResult,
            resultTypeName,
            DescribeParameters(symbol.Parameters)
        );
    }

    /// <summary>
    /// Spells every parameter as <c>(type, name, isComplex)</c> — the three facts the emitters
    /// and the route builder ask of a parameter. <c>IsComplex</c> is decided here because
    /// <see cref="IsComplexType(ITypeSymbol)"/> needs the symbol; the emitter only needs the
    /// answer.
    /// </summary>
    private static EquatableArray<ParameterModel> DescribeParameters(
        ImmutableArray<IParameterSymbol> parameters
    )
    {
        if (parameters.IsDefaultOrEmpty)
            return EquatableArray<ParameterModel>.Empty;

        var described = new ParameterModel[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            described[i] = new ParameterModel(
                parameters[i].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                parameters[i].Name,
                IsComplexType(parameters[i].Type)
            );
        }

        return new EquatableArray<ParameterModel>(described);
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

    /// <summary>
    /// The action's own route segment, from the verb attribute or from the method name.
    /// <para>
    /// The attribute is read through the <em>semantic</em> model (task T5.7, audit F-56). Reading
    /// its syntax text (<c>arg.ToString().Trim('"')</c>) gave <c>[HttpGet(Routes.Posts)]</c> the
    /// route <c>"Routes.Posts"</c> — a literal path that no request can ever match, emitted with a
    /// clean build. <see cref="AttributeData.ConstructorArguments"/> carry the constant's value,
    /// which is the string the user wrote.
    /// </para>
    /// </summary>
    private static string GetRoute(IMethodSymbol methodSymbol)
    {
        // Check for [HttpGet("posts")] / [HttpGet(Routes.Posts)] attribute override. An attribute
        // written without parentheses ([HttpGet]) is filled with its parameter default, so the
        // first constructor argument is either the route or the empty string that means "use the
        // controller prefix".
        foreach (var attr in methodSymbol.GetAttributes())
        {
            if (!IsHttpVerbAttribute(attr.AttributeClass?.Name))
                continue;

            var argument = attr.ConstructorArguments.FirstOrDefault();
            return argument.Value as string ?? "";
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
            && methodName.EndsWith(lastParam.Name, StringComparison.OrdinalIgnoreCase)
        )
        {
            methodName = methodName.Substring(0, methodName.Length - lastParam.Name.Length);
        }

        // If method name is empty after stripping prefix, omit the method path segment
        var route = string.IsNullOrEmpty(methodName) ? "" : ToKebabCase(methodName);
        if (routeParams.Count > 0)
            route += "/" + string.Join("/", routeParams);
        return route;
    }

    /// <summary>
    /// The five attributes that carry an HTTP verb — matched by class <em>name</em>, which is the
    /// convention documented on <c>PicoNode.Web.ControllerAttributes</c> (a sample may declare its
    /// own stubs, and this generator honours them).
    /// </summary>
    private static bool IsHttpVerbAttribute(string? attributeName) =>
        attributeName
            is "HttpGetAttribute"
                or "HttpPostAttribute"
                or "HttpPutAttribute"
                or "HttpDeleteAttribute"
                or "HttpPatchAttribute";

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
                {
                    // Normalise the leading slash (task T5.7): WebRouter rejects a pattern that
                    // does not start with '/' with an ArgumentException inside WebApp.Build(), so
                    // an attribute written as [Route("api/x")] used to take the app down at
                    // startup. `[Route("/")]` normalises to "/" rather than to "".
                    return "/" + route.TrimStart('/');
                }
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

    /// <summary>
    /// The identifier of a controller's generated endpoint stub, derived from the controller's
    /// <em>fully-qualified</em> name.
    /// <para>
    /// Keyed by the simple name — <c>controller.Name + "_Endpoints"</c> — the stub collided
    /// across namespaces: <c>Admin.UsersController</c> and <c>Public.UsersController</c> both
    /// produced a global <c>UsersController_Endpoints</c> in the same generated file, which is
    /// CS0101 ("the global namespace already contains a definition"), plus CS0111 and two
    /// CS0121s from the registrar — in a <em>single</em> compilation (audit F-53). The stub is
    /// emitted at global scope, so its name has to carry the namespace.
    /// </para>
    /// <para>
    /// Every character that cannot appear in an identifier becomes <c>_</c>. The
    /// <c>global::</c> prefix is dropped because it carries no identity; <c>.</c>, <c>+</c>,
    /// <c>&lt;</c>, <c>&gt;</c>, <c>,</c> and spaces all collapse to one <c>_</c> each. The
    /// mapping is not injective in pathological cases — a nested <c>B_C</c> reads like
    /// <c>C</c> inside namespace <c>B</c> — which the task report records rather than hides;
    /// the audit's shape only needs distinct namespaces.
    /// </para>
    /// </summary>
    private static string StubClassName(string fullyQualifiedName)
    {
        const string GlobalPrefix = "global::";
        var start = fullyQualifiedName.StartsWith(GlobalPrefix, StringComparison.Ordinal)
            ? GlobalPrefix.Length
            : 0;

        var sb = new StringBuilder(fullyQualifiedName.Length + 10);
        for (var i = start; i < fullyQualifiedName.Length; i++)
        {
            var c = fullyQualifiedName[i];
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }
        sb.Append("_Endpoints");
        return sb.ToString();
    }

    /// <summary>
    /// Reports every diagnostic the collected models carry, once per site.
    /// <para>
    /// This is the generator's only channel for telling the user something about code it did
    /// <em>not</em> map (task T5.7, audit F-55). Reporting happens in the source output because a
    /// <see cref="SourceProductionContext"/> exists only here — and, like the note it replaces, it
    /// happens before any early return, so the "nothing emittable" path still explains itself.
    /// </para>
    /// <para>
    /// Deduplicated by <see cref="DiagnosticModel.Key"/>, so two controllers that serialise the
    /// same DTO produce one warning at that DTO rather than one per controller.
    /// </para>
    /// </summary>
    private static void ReportDiagnostics(
        SourceProductionContext context,
        System.Collections.Immutable.ImmutableArray<ControllerModel> controllers
    )
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var controller in controllers)
        {
            foreach (var diagnostic in controller.Diagnostics)
            {
                if (!reported.Add(diagnostic.Key))
                    continue;

                var descriptor = Diagnostics.DescriptorFor(diagnostic.DescriptorId);
                if (descriptor is null)
                    continue;

                context.ReportDiagnostic(
                    Diagnostic.Create(descriptor, diagnostic.ToLocation(), diagnostic.Name)
                );
            }
        }
    }

    /// <summary>
    /// The type an action hands to <c>PicoJetson.JsonSerializer.SerializeToUtf8Bytes</c> that has
    /// no <c>[PicoJsonSerializable]</c> — or <c>null</c> when the action serialises nothing, or
    /// what it serialises is fine.
    /// <para>
    /// The position is the emitter's, not a guess: <c>void</c>/<c>Task</c>/<c>ValueTask</c> answer
    /// 204, an <c>IWebResult</c> is executed, and only what is left is serialised. Declared-here
    /// types only — a BCL type has no attribute to add, so warning about it would be noise (see
    /// <see cref="UnmarkedDeclaredType"/>).
    /// </para>
    /// </summary>
    private static INamedTypeSymbol? UnmarkedSerializedType(IMethodSymbol symbol)
    {
        var returnType = symbol.ReturnType;
        if (returnType.SpecialType == SpecialType.System_Void)
            return null;

        if (ReturnsIWebResult(returnType))
            return null;

        if (returnType is INamedTypeSymbol { Name: "Task" or "ValueTask" } task)
        {
            if (task.TypeArguments.Length != 1)
                return null;

            return UnmarkedDeclaredType(task.TypeArguments[0], symbol);
        }

        return UnmarkedDeclaredType(returnType, symbol);
    }

    /// <summary>
    /// The first type in <paramref name="type"/>'s own graph that is declared in this compilation
    /// and is not marked <c>[PicoJsonSerializable]</c>.
    /// <para>
    /// The graph is walked because a serializer is needed for the <em>arguments</em> too:
    /// <c>List&lt;PostDto&gt;</c> is serialised by needing <c>PostDto</c>'s serializer, and that is
    /// the type the attribute belongs on. The type itself is considered after its arguments, so a
    /// framework generic (<c>List&lt;&gt;</c>, <c>Dictionary&lt;,&gt;</c>) contributes nothing but
    /// what it is made of.
    /// </para>
    /// </summary>
    private static INamedTypeSymbol? UnmarkedDeclaredType(ITypeSymbol type, IMethodSymbol context)
    {
        if (type is IArrayTypeSymbol array)
            return UnmarkedDeclaredType(array.ElementType, context);

        if (type is not INamedTypeSymbol named)
            return null;

        foreach (var argument in named.TypeArguments)
        {
            var unmarked = UnmarkedDeclaredType(argument, context);
            if (unmarked is not null)
                return unmarked;
        }

        // Declared in *this* compilation: a type from a referenced project is that project's to
        // annotate, and a BCL type cannot be annotated at all.
        var declaredHere =
            named.TypeKind is TypeKind.Class or TypeKind.Struct
            && SymbolEqualityComparer.Default.Equals(
                named.ContainingAssembly,
                context.ContainingAssembly
            );

        if (!declaredHere)
            return null;

        var marked = named
            .GetAttributes()
            .Any(static a => a.AttributeClass?.Name == "PicoJsonSerializableAttribute");

        return marked ? null : named;
    }

    /// <summary>
    /// The identifier of a declaration — the place the user has to edit, so the place a diagnostic
    /// has to point at. A symbol with no source declaration has none, and
    /// <see cref="DiagnosticModel.Create"/> then refuses the diagnostic rather than reporting one
    /// nobody can navigate to.
    /// </summary>
    private static Location? IdentifierLocation(INamedTypeSymbol type)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is TypeDeclarationSyntax declaration)
                return declaration.Identifier.GetLocation();
        }

        return type.Locations.FirstOrDefault(static location => location.IsInSource);
    }

    private static void GenerateSources(
        SourceProductionContext context,
        System.Collections.Immutable.ImmutableArray<ControllerModel> controllers
    )
    {
        // Skipped controllers never reach an emitter: a stub for `ItemsController<T>` is
        // precisely the unbound `T` this fix exists to stop emitting.
        var emitted = controllers.Where(static c => c.SkipReason is null).ToArray();

        var registrarCode = new StringBuilder();
        registrarCode.AppendLine("#nullable enable");

        if (emitted.Length == 0)
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

        foreach (var controller in emitted)
        {
            var stubClassName = StubClassName(controller.FullName);

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
                // Empty method route (e.g. [HttpPost] without path arg) → use prefix as-is
                // Absolute route (starts with /) → replaces the prefix entirely
                // Relative route → appended with a slash separator
                var fullRoute =
                    string.IsNullOrEmpty(methodRoute) ? controller.RoutePrefix
                    : methodRoute.StartsWith("/") ? methodRoute
                    : controller.RoutePrefix + "/" + methodRoute;

                // The return shape was analysed in the transform (see DescribeMethod): the
                // emitter reads decisions, never a symbol.
                var returnsIWebResult = method.IsWebResult;
                var isAsync = method.IsAsync;
                var returnsValue = method.ReturnsValue;
                var resultTypeName = method.ResultTypeName;

                var asyncPrefix = isAsync ? "async " : "";
                endpointsCode.AppendLine(
                    $"            app.{mapMethod}(\"{fullRoute}\", {asyncPrefix}(WebContext ctx, CancellationToken ct) =>"
                );
                endpointsCode.AppendLine("            {");

                // Bind route parameters and DI services
                var callArgs = new List<string>();
                foreach (var param in method.Parameters)
                {
                    var typeName = param.TypeName;
                    var paramName = param.Name;

                    // Special parameter types passed directly, not resolved from DI
                    if (typeName == "global::PicoNode.Web.WebContext")
                    {
                        callArgs.Add("ctx");
                        continue;
                    }
                    if (typeName == "global::System.Threading.CancellationToken")
                    {
                        callArgs.Add("ct");
                        continue;
                    }

                    if (!param.IsComplex)
                    {
                        endpointsCode.AppendLine(
                            $"                {GetRouteValueBinding(typeName, paramName, isAsync)}"
                        );
                    }
                    else
                    {
                        // Complex type — resolve from DI. The cast is mandatory:
                        // ISvcScope.GetService returns object?, so an uncast result is
                        // CS1503 the moment it is passed to the action's typed parameter
                        // (audit F-51). Same shape as the constructor-parameter path
                        // below (:536) — that path always cast, which is why ctor
                        // injection compiled while an action parameter did not.
                        endpointsCode.AppendLine(
                            $"                var __{paramName} = ({typeName})ctx.Services.GetService(typeof({typeName}))!;"
                        );
                    }
                    callArgs.Add($"__{paramName}");
                }

                // A static member needs no instance, and cannot have one (audit F-52).
                // A `static class` controller used to be treated as an instance: the
                // emitted `((StatusController)__controller).Get()` is CS0716 (cannot
                // convert to static type), and the `GetService` beside it would return
                // null, because a static controller is deliberately never registered in
                // DI (see the IsStatic skip in the registrations block below). A `static`
                // method on an ordinary controller produces the same defect as CS0176
                // ("cannot be accessed with an instance reference"), so the discriminator
                // is the member, not the class: `MethodModel.IsStatic` is true for both
                // shapes and false for every instance action.
                var invokeStatically = method.IsStatic;

                // No DI decision to make for a static target: a `static class` cannot
                // declare an instance constructor (CS0710/CS0711 — reported in the user's
                // own file before any generation), so there is no constructor injection to
                // honour and no instance to resolve. Action parameters are unaffected: a
                // complex parameter still resolves from DI below, which does not depend on
                // the controller instance. A static controller that needs a service must
                // therefore take it as an action parameter (or be made an instance
                // controller) — constructor injection is a compile error in its own source,
                // never something this generator silently drops.
                if (!invokeStatically)
                {
                    endpointsCode.AppendLine(
                        $"                var __controller = ctx.Services.GetService(typeof({controller.FullName}))!;"
                    );
                }

                // The instance target keeps the long-standing shape (every use site
                // re-casts the untyped `__controller`); a static target is qualified by its
                // full name and called directly — no DI resolve, no `new`, no cast.
                var invocationTarget = invokeStatically
                    ? controller.FullName
                    : $"(({controller.FullName})__controller)";

                var awaitPrefix = isAsync ? "await " : "";

                if (!returnsValue)
                {
                    endpointsCode.AppendLine(
                        $"                {awaitPrefix}{invocationTarget}.{method.Name}({string.Join(", ", callArgs)});"
                    );
                    if (isAsync)
                        endpointsCode.AppendLine(
                            "                return PicoWeb.Results.Empty(204);"
                        );
                    else
                        endpointsCode.AppendLine(
                            "                return ValueTask.FromResult(PicoWeb.Results.Empty(204));"
                        );
                }
                else if (returnsIWebResult)
                {
                    endpointsCode.AppendLine(
                        $"                var __result = ({resultTypeName}){awaitPrefix}{invocationTarget}.{method.Name}({string.Join(", ", callArgs)});"
                    );
                    if (isAsync)
                        endpointsCode.AppendLine("                return __result.Execute(ctx);");
                    else
                        endpointsCode.AppendLine(
                            "                return ValueTask.FromResult(__result.Execute(ctx));"
                        );
                }
                else
                {
                    endpointsCode.AppendLine(
                        $"                var __result = ({resultTypeName}){awaitPrefix}{invocationTarget}.{method.Name}({string.Join(", ", callArgs)});"
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

        foreach (var controller in emitted)
        {
            var fqn = controller.FullName;
            if (controller.IsStatic)
                continue;

            diCode.AppendLine(
                "        container.Register(global::PicoDI.Abs.SvcDescriptor.Create("
            );
            diCode.AppendLine($"            typeof({fqn}),");

            if (controller.ConstructorParameters.IsEmpty)
            {
                diCode.AppendLine($"            static _ => new {fqn}(),");
            }
            else
            {
                diCode.AppendLine("            scope =>");
                diCode.AppendLine("            {");

                var ctorArgs = new List<string>();
                foreach (var param in controller.ConstructorParameters)
                {
                    var paramType = param.TypeName;
                    var paramName = param.Name;
                    diCode.AppendLine(
                        $"                var __{paramName} = ({paramType})scope.GetService(typeof({paramType}))!;"
                    );
                    ctorArgs.Add($"__{paramName}");
                }
                diCode.AppendLine(
                    $"                return new {fqn}({string.Join(", ", ctorArgs)});"
                );
                diCode.AppendLine("            },");
            }

            diCode.AppendLine("            global::PicoDI.Abs.SvcLifetime.Scoped));");
            diCode.AppendLine();
        }

        diCode.AppendLine("    }");
        diCode.AppendLine("}");

        context.AddSource("ControllerServiceRegistrations.g.cs", diCode.ToString());
    }

    /// <summary>
    /// Emits the parameter binding for a simple-typed parameter: route value
    /// first, then query string, then 400 when neither carries the name.
    /// Reason (audit F-02): a `[HttpPost]` action such as `Create(string title)`
    /// is emitted as the bare prefix route `/api/posts`, so `title` has NO route
    /// segment — the old unguarded `ctx.RouteValues["title"]` indexer threw
    /// KeyNotFoundException → 500. No `{title}` segment may be invented for it:
    /// RadixTree allows one parameter child per node, so `POST /api/posts/{title}`
    /// beside `DELETE /api/posts/{id}` aborts the router with "Duplicate route
    /// registration".
    /// Non-string simple types keep their TryParse guard and still answer 404
    /// when the located value cannot be converted (PicoWeb has no route
    /// constraints, so {id} matches ANY segment — an unguarded Guid.Parse(img.png)
    /// threw FormatException → 500; regression: browser requesting
    /// /fragments/sessions/img.png for a relative markdown image).
    /// </summary>
    private static string GetRouteValueBinding(string typeName, string paramName, bool isAsync)
    {
        var inv = "System.Globalization.CultureInfo.InvariantCulture";
        // Async lambda: HttpResponse converts implicitly to ValueTask<HttpResponse>;
        // sync lambda must wrap explicitly (its body returns ValueTask<T> directly).
        var badRequest = isAsync
            ? "PicoWeb.Results.Empty(400)"
            : "ValueTask.FromResult(PicoWeb.Results.Empty(400))";
        var notFound = isAsync
            ? "PicoWeb.Results.Empty(404)"
            : "ValueTask.FromResult(PicoWeb.Results.Empty(404))";

        var isString = typeName is "string" or "global::System.String";
        // A string binds the located value directly; every other simple type
        // needs the raw string for TryParse plus the parsed local.
        var lookupLocal = isString ? $"__{paramName}" : $"__{paramName}_raw";
        var lookup =
            $"if (!ctx.RouteValues.TryGetValue(\"{paramName}\", out var {lookupLocal})\n"
            + $"                    && !ctx.Query.TryGetValue(\"{paramName}\", out {lookupLocal}))\n"
            + $"                    return {badRequest};";

        if (isString)
            return lookup;

        var tryParse = typeName switch
        {
            "int" or "global::System.Int32" =>
                $"int.TryParse({lookupLocal}, System.Globalization.NumberStyles.Integer, {inv}, out var __{paramName})",
            "long" or "global::System.Int64" =>
                $"long.TryParse({lookupLocal}, System.Globalization.NumberStyles.Integer, {inv}, out var __{paramName})",
            "short" or "global::System.Int16" =>
                $"short.TryParse({lookupLocal}, System.Globalization.NumberStyles.Integer, {inv}, out var __{paramName})",
            "ushort" or "global::System.UInt16" =>
                $"ushort.TryParse({lookupLocal}, System.Globalization.NumberStyles.Integer, {inv}, out var __{paramName})",
            "uint" or "global::System.UInt32" =>
                $"uint.TryParse({lookupLocal}, System.Globalization.NumberStyles.Integer, {inv}, out var __{paramName})",
            "ulong" or "global::System.UInt64" =>
                $"ulong.TryParse({lookupLocal}, System.Globalization.NumberStyles.Integer, {inv}, out var __{paramName})",
            "byte" or "global::System.Byte" =>
                $"byte.TryParse({lookupLocal}, System.Globalization.NumberStyles.Integer, {inv}, out var __{paramName})",
            "sbyte" or "global::System.SByte" =>
                $"sbyte.TryParse({lookupLocal}, System.Globalization.NumberStyles.Integer, {inv}, out var __{paramName})",
            "float" or "global::System.Single" =>
                $"float.TryParse({lookupLocal}, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands, {inv}, out var __{paramName})",
            "double" or "global::System.Double" =>
                $"double.TryParse({lookupLocal}, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands, {inv}, out var __{paramName})",
            "decimal" or "global::System.Decimal" =>
                $"decimal.TryParse({lookupLocal}, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands, {inv}, out var __{paramName})",
            "bool" or "global::System.Boolean" => $"bool.TryParse({lookupLocal}, out var __{paramName})",
            "char" or "global::System.Char" => $"char.TryParse({lookupLocal}, out var __{paramName})",
            "global::System.Guid" => $"global::System.Guid.TryParse({lookupLocal}, out var __{paramName})",
            "global::System.DateTime" =>
                $"global::System.DateTime.TryParse({lookupLocal}, {inv}, System.Globalization.DateTimeStyles.None, out var __{paramName})",
            "global::System.DateTimeOffset" =>
                $"global::System.DateTimeOffset.TryParse({lookupLocal}, {inv}, System.Globalization.DateTimeStyles.None, out var __{paramName})",
            "global::System.TimeSpan" =>
                $"global::System.TimeSpan.TryParse({lookupLocal}, {inv}, out var __{paramName})",
            "global::System.DateOnly" =>
                $"global::System.DateOnly.TryParse({lookupLocal}, {inv}, System.Globalization.DateTimeStyles.None, out var __{paramName})",
            "global::System.TimeOnly" =>
                $"global::System.TimeOnly.TryParse({lookupLocal}, {inv}, System.Globalization.DateTimeStyles.None, out var __{paramName})",
            _ => $"System.Enum.TryParse<{typeName}>({lookupLocal}, out var __{paramName})",
        };

        return lookup
            + "\n                "
            + $"if (!{tryParse})\n                    return {notFound};";
    }

    private static bool ReturnsIWebResult(ITypeSymbol returnType)
    {
        if (returnType is not INamedTypeSymbol named)
            return false;

        bool ImplementsIWebResult(INamedTypeSymbol t) =>
            t.AllInterfaces.Any(i =>
                i.Name == "IWebResult" && i.ContainingNamespace.ToDisplayString() == "PicoNode.Web"
            )
            || (
                t.Name == "IWebResult" && t.ContainingNamespace.ToDisplayString() == "PicoNode.Web"
            );

        if (ImplementsIWebResult(named))
            return true;

        if (
            (named.Name is "ValueTask" or "Task")
            && named.TypeArguments.Length == 1
            && named.TypeArguments[0] is INamedTypeSymbol inner
        )
        {
            return ImplementsIWebResult(inner);
        }

        return false;
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

/// <summary>
/// The project-level questions, asked as values.
/// <para>
/// This is what replaced carrying a <see cref="Compilation"/> through the pipeline (task T5.6).
/// A compilation is not a comparable value — and everything reachable from it (symbols, syntax
/// trees) drags the whole graph along — so a step that hands one on can never be cached. Here
/// it is read in one <c>Select</c> and reduced to two bools immediately; nothing downstream
/// ever sees it again.
/// </para>
/// <para>
/// A <c>record</c> for the same reason as the models below: the driver decides whether a step
/// is cached by comparing the value it produced with the previous one, so a value without
/// structural equality is a step that always re-runs.
/// </para>
/// </summary>
internal sealed record CompilationState
{
    /// <summary>
    /// ConsoleApplication / WindowsApplication that references <c>PicoNode.Web</c> — the two
    /// conditions for generating endpoints at all, kept together because nothing ever asks
    /// about one of them alone.
    /// </summary>
    public bool CanHostEndpoints { get; }

    /// <summary>
    /// An <c>EndpointRegistrar</c> already exists in this compilation — typically an Exe that
    /// references another app's generated public registrar.
    /// </summary>
    public bool HasEndpointRegistrar { get; }

    public CompilationState(bool canHostEndpoints, bool hasEndpointRegistrar)
    {
        CanHostEndpoints = canHostEndpoints;
        HasEndpointRegistrar = hasEndpointRegistrar;
    }
}

/// <summary>
/// One discovered controller, described entirely by values.
/// <para>
/// Nothing here is a symbol, a syntax node or a compilation: every string the emitter needs is
/// spelled out in the transform (<c>DescribeMethod</c>), which is what makes two runs over
/// unchanged source produce <em>equal</em> models and lets the driver cache the step. The
/// equality is the record's synthesized one — every property, including the two
/// <see cref="EquatableArray{T}"/> collections, compares by value — so a field added here is
/// part of the comparison automatically instead of being the next thing someone forgets.
/// </para>
/// </summary>
internal sealed record ControllerModel
{
    /// <summary>The simple name, kept for diagnostics and readable step output.</summary>
    public string Name { get; }

    /// <summary>The fully-qualified name, exactly as it is emitted (<c>global::Ns.Type</c>).</summary>
    public string FullName { get; }

    /// <summary>The route prefix, from <c>[Route]</c> or the <c>XxxController</c> convention.</summary>
    public string RoutePrefix { get; }

    public EquatableArray<MethodModel> Methods { get; }

    public bool IsStatic { get; }

    /// <summary>
    /// The injected constructor parameters, already spelled as
    /// <c>(type, name, isComplex)</c> — the emitters never need the symbol they came from.
    /// </summary>
    public EquatableArray<ParameterModel> ConstructorParameters { get; }

    /// <summary>
    /// Why this controller is <em>not</em> emitted, or <c>null</c> when it is. A skipped model
    /// carries no methods and reaches no emitter (see <c>GenerateSources</c>); it exists so the
    /// skip is a value in the pipeline rather than a silent <c>return null</c>. What the user
    /// actually sees is the <see cref="Diagnostics"/> entry beside it (task T5.7).
    /// </summary>
    public string? SkipReason { get; }

    /// <summary>
    /// What to report about this controller, as values: a skipped controller's reason
    /// (<c>PCW001</c>/<c>PCW002</c>), or a serialisation problem in one of its actions
    /// (<c>PCW003</c>). Empty for the ordinary case.
    /// </summary>
    public EquatableArray<DiagnosticModel> Diagnostics { get; }

    public ControllerModel(
        string name,
        string fullName,
        string routePrefix,
        EquatableArray<MethodModel> methods,
        bool isStatic,
        EquatableArray<ParameterModel> constructorParameters,
        EquatableArray<DiagnosticModel> diagnostics,
        string? skipReason = null
    )
    {
        Name = name;
        FullName = fullName;
        RoutePrefix = routePrefix;
        Methods = methods;
        IsStatic = isStatic;
        ConstructorParameters = constructorParameters;
        Diagnostics = diagnostics;
        SkipReason = skipReason;
    }

    /// <summary>A controller that matched the predicate but cannot be emitted.</summary>
    public static ControllerModel Skipped(
        string name,
        string fullName,
        string reason,
        DiagnosticModel? diagnostic
    ) =>
        new(
            name,
            fullName,
            "",
            EquatableArray<MethodModel>.Empty,
            false,
            EquatableArray<ParameterModel>.Empty,
            diagnostic is null
                ? EquatableArray<DiagnosticModel>.Empty
                : new EquatableArray<DiagnosticModel>([diagnostic]),
            reason
        );
}

/// <summary>
/// One mapped action, as values. The decisions the emitter makes — <c>async</c> or not, does it
/// return a value, is that value an <c>IWebResult</c>, what is the result type spelled as — are
/// made in the transform, where the <see cref="IMethodSymbol"/> still exists.
/// <para>
/// This record used to hold that <see cref="IMethodSymbol"/> itself. That single field is what
/// made the whole pipeline non-incremental: a symbol is not comparable, so every model was
/// "new" on every run and every emitter below it re-ran on every keystroke.
/// </para>
/// </summary>
internal sealed record MethodModel
{
    /// <summary><c>GET</c> / <c>POST</c> / <c>PUT</c> / <c>DELETE</c> / <c>PATCH</c>.</summary>
    public string HttpMethod { get; }

    /// <summary>The action's own route segment, before the controller prefix is applied.</summary>
    public string Route { get; }

    /// <summary>The C# member name to invoke.</summary>
    public string Name { get; }

    /// <summary>
    /// A <c>static</c> member is invoked through its type, not through the resolved instance —
    /// the discriminator is the member, not the class (audit F-52).
    /// </summary>
    public bool IsStatic { get; }

    /// <summary>
    /// The declared return type is <c>Task</c>/<c>ValueTask</c>, so the lambda is <c>async</c>.
    /// </summary>
    public bool IsAsync { get; }

    /// <summary>
    /// The action produces a value to execute or serialise. <c>false</c> for <c>void</c>,
    /// <c>Task</c> and <c>ValueTask</c> — the emitted body then answers 204.
    /// </summary>
    public bool ReturnsValue { get; }

    /// <summary>
    /// The returned value implements <c>PicoNode.Web.IWebResult</c>, so the emitted body calls
    /// <c>.Execute(ctx)</c> instead of serialising.
    /// </summary>
    public bool IsWebResult { get; }

    /// <summary>
    /// The result type, spelled with <c>SymbolDisplayFormat.FullyQualifiedFormat</c> (the
    /// <c>Task</c>/<c>ValueTask</c> wrapper already unwrapped). Empty when
    /// <see cref="ReturnsValue"/> is false — it is never emitted in that case.
    /// </summary>
    public string ResultTypeName { get; }

    public EquatableArray<ParameterModel> Parameters { get; }

    public MethodModel(
        string httpMethod,
        string route,
        string name,
        bool isStatic,
        bool isAsync,
        bool returnsValue,
        bool isWebResult,
        string resultTypeName,
        EquatableArray<ParameterModel> parameters
    )
    {
        HttpMethod = httpMethod;
        Route = route;
        Name = name;
        IsStatic = isStatic;
        IsAsync = isAsync;
        ReturnsValue = returnsValue;
        IsWebResult = isWebResult;
        ResultTypeName = resultTypeName;
        Parameters = parameters;
    }
}

/// <summary>
/// One parameter of an action or of the injected constructor, as values: the type exactly as it
/// is emitted, the name, and the one classification the emitter needs instead of the type
/// symbol (<see cref="IsComplex"/> — see <c>IsComplexType</c>).
/// </summary>
internal sealed record ParameterModel
{
    /// <summary>The type, spelled with <c>SymbolDisplayFormat.FullyQualifiedFormat</c>.</summary>
    public string TypeName { get; }

    public string Name { get; }

    /// <summary>
    /// Not a simple/bindable type: it is resolved from DI (and cast) rather than parsed out of
    /// the route or the query string.
    /// </summary>
    public bool IsComplex { get; }

    public ParameterModel(string typeName, string name, bool isComplex)
    {
        TypeName = typeName;
        Name = name;
        IsComplex = isComplex;
    }
}
