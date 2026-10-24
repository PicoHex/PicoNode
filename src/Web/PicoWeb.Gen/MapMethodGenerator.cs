using Microsoft.CodeAnalysis.Text;

namespace PicoWeb.Gen;

[Generator(LanguageNames.CSharp)]
public sealed class MapMethodGenerator : IIncrementalGenerator
{
    /// <summary>
    /// <c>PWR001</c>, declared once (task T5.7, audit F-55/F-56).
    /// <para>
    /// It used to be built inside the reporting loop — a new <see cref="DiagnosticDescriptor"/>
    /// per diagnostic — with <c>defaultSeverity: Info</c> and <c>location: null</c>, which made it
    /// invisible twice over: an IDE cannot navigate to a location-less diagnostic and a build log
    /// does not show <c>Info</c>. It is a <c>Warning</c> at the call site now.
    /// </para>
    /// </summary>
    private static readonly DiagnosticDescriptor NonSerializableReturnType = new(
        id: "PWR001",
        title: "Route handler return type",
        messageFormat: "Route handler returns '{0}'. Ensure it is serializable if used as JSON response.",
        category: "PicoWeb",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "PicoWeb.Gen emits no source. This diagnostic exists so that a route handler whose return type will be serialised is visible at build time instead of at request time."
    );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var mapCalls = context
            .SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => IsInvocationCandidate(node),
                transform: static (ctx, ct) => GetReturnType(ctx, ct)
            )
            .Where(static t => t is not null)
            .Select(static (t, _) => t!);

        var collected = mapCalls.Collect();

        context.RegisterSourceOutput(
            collected,
            static (spc, returns) => ReportReturnTypes(spc, returns)
        );
    }

    private static bool IsInvocationCandidate(SyntaxNode node)
    {
        if (
            node is InvocationExpressionSyntax invocation
            && invocation.Expression is MemberAccessExpressionSyntax memberAccess
            && (
                memberAccess.Name.Identifier.Text == "MapGet"
                || memberAccess.Name.Identifier.Text == "MapPost"
                || memberAccess.Name.Identifier.Text == "MapPut"
                || memberAccess.Name.Identifier.Text == "MapDelete"
                // `MapPatch` was missing from this list, so a PATCH registration was never
                // inspected at all (task T5.7).
                || memberAccess.Name.Identifier.Text == "MapPatch"
            )
        )
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Whether this <c>MapGet</c>-shaped call is a route registration, rather than a method that
    /// merely shares the name (audit S4: any unrelated type's own <c>MapGet</c> was reported as a
    /// route handler). The syntax predicate can only see a name — it has no
    /// <see cref="SemanticModel"/> — so the symbol is asked here, in the transform.
    /// </summary>
    private static bool IsRouteRegistration(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken ct
    )
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return false;

        var method = semanticModel.GetSymbolInfo(memberAccess, ct).Symbol as IMethodSymbol;
        var containingType = method?.ContainingType;
        if (containingType is null)
            return false;

        var containingNamespace = containingType.ContainingNamespace?.ToDisplayString();
        return (containingNamespace, containingType.Name) switch
        {
            // app.MapGet(…) — the PicoWeb application surface.
            ("PicoNode.Web", "WebApp") => true,
            // Route<T>.MapGet(…) — the protocol layer's route registration.
            ("PicoNode.Http", "Route") => true,
            _ => false,
        };
    }

    private static RouteReturnType? GetReturnType(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        var semanticModel = ctx.SemanticModel;

        if (!IsRouteRegistration(invocation, semanticModel, ct))
            return null;

        // Get the last argument (lambda or method reference)
        var lastArg = invocation.ArgumentList.Arguments.LastOrDefault();
        if (lastArg == null)
            return null;

        // A handler that does not convert to the expected delegate has no usable return type
        // (and the compiler already says so) — nothing to report here.
        if (semanticModel.GetTypeInfo(lastArg.Expression, ct).ConvertedType == null)
            return null;

        SyntaxNode? body = lastArg.Expression switch
        {
            SimpleLambdaExpressionSyntax simpleLambda => simpleLambda.Body,
            ParenthesizedLambdaExpressionSyntax parenLambda => parenLambda.Body,
            _ => null,
        };

        if (body is null)
            return null;

        // A block body has no single expression to inspect — the old code read the lambda's body
        // *expression*, so `ctx => { return …; }` was silently unchecked (task T5.7). The first
        // `return` decides; a handler that returns at several points is reported once, for the
        // first of them.
        var resultType = body is BlockSyntax block
            ? BlockReturnType(block, semanticModel, ct)
            : BodyReturnType(body, semanticModel, ct);

        if (resultType is null)
            return null;

        return RouteReturnType.At(invocation, resultType);
    }

    private static string? BodyReturnType(
        SyntaxNode body,
        SemanticModel semanticModel,
        CancellationToken ct
    )
    {
        var typeInfo = semanticModel.GetTypeInfo(body, ct);
        if (typeInfo.ConvertedType == null)
            return null;

        // ValueTask<T> is the handler's envelope, not its payload: report T.
        if (
            typeInfo.ConvertedType is INamedTypeSymbol { Name: "ValueTask" } namedType
            && namedType.TypeArguments.Length == 1
        )
            return GetResultTypeName(namedType.TypeArguments[0]);

        return GetResultTypeName(typeInfo.ConvertedType);
    }

    private static string? BlockReturnType(
        BlockSyntax block,
        SemanticModel semanticModel,
        CancellationToken ct
    )
    {
        foreach (var statement in block.Statements)
        {
            if (statement is ReturnStatementSyntax { Expression: { } expression })
                return BodyReturnType(expression, semanticModel, ct);
        }

        return null;
    }

    private static string? GetResultTypeName(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol)
        {
            var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (!fullName.StartsWith("global::System"))
                return fullName;
        }
        return null;
    }

    private static void ReportReturnTypes(
        SourceProductionContext context,
        ImmutableArray<RouteReturnType> returns
    )
    {
        if (returns.IsDefaultOrEmpty)
            return;

        // Emit compile-time diagnostics for route handler return types.
        // This helps developers verify serialization readiness at build time.
        // NOTE: no source files are generated — the former RouteReturnTypes
        // class was dead code (nothing ever read it) and only bloated
        // consumer assemblies, so it was removed.
        //
        // One diagnostic per *type*, as before, but now reported at the registration that
        // produced it: five handlers returning the same DTO are one problem to fix, and repeating
        // that warning five times is how a rule gets suppressed.
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var routeReturn in returns)
        {
            // A handler that answers `HttpResponse` (or a `ValueTask<…>` of it) is the normal
            // shape: the framework builds the response, nothing is serialised here.
            if (
                routeReturn.TypeName.StartsWith(
                    "global::PicoNode.Http.HttpResponse",
                    StringComparison.Ordinal
                )
                || routeReturn.TypeName.StartsWith(
                    "global::System.Threading.Tasks.ValueTask",
                    StringComparison.Ordinal
                )
            )
                continue;

            if (!reported.Add(routeReturn.TypeName))
                continue;

            context.ReportDiagnostic(
                Diagnostic.Create(
                    NonSerializableReturnType,
                    routeReturn.ToLocation(),
                    routeReturn.TypeName
                )
            );
        }
    }
}

/// <summary>
/// One route registration's handler return type, as values: the name to report and the place to
/// report it.
/// <para>
/// A <see cref="Location"/> cannot travel through an incremental pipeline — it has no value
/// equality, so every run would look "modified" and the step would never be cached (the same
/// constraint T5.6 documented for the controller models). The path, the span and the line span are
/// the location split into comparable parts; <see cref="ToLocation"/> rebuilds it where the
/// <see cref="SourceProductionContext"/> is.
/// </para>
/// </summary>
internal sealed record RouteReturnType
{
    /// <summary>The handler's result type, spelled as it is reported.</summary>
    public string TypeName { get; }

    public string FilePath { get; }

    public TextSpan Span { get; }

    public LinePositionSpan LineSpan { get; }

    public RouteReturnType(
        string typeName,
        string filePath,
        TextSpan span,
        LinePositionSpan lineSpan
    )
    {
        TypeName = typeName;
        FilePath = filePath;
        Span = span;
        LineSpan = lineSpan;
    }

    /// <summary>
    /// The type found at <paramref name="node"/>, or <c>null</c> when the node has no source
    /// location — a diagnostic nobody can navigate to is not worth reporting.
    /// </summary>
    internal static RouteReturnType? At(SyntaxNode node, string typeName)
    {
        var location = node.GetLocation();
        if (!location.IsInSource || location.SourceTree is null)
            return null;

        var lineSpan = location.GetLineSpan();
        return new RouteReturnType(typeName, lineSpan.Path, location.SourceSpan, lineSpan.Span);
    }

    internal Location ToLocation() => Location.Create(FilePath, Span, LineSpan);
}
