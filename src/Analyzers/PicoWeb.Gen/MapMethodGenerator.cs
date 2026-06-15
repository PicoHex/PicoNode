using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;

namespace PicoWeb.Gen;

[Generator(LanguageNames.CSharp)]
public sealed class MapMethodGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var mapCalls = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => IsInvocationCandidate(node),
            transform: static (ctx, ct) => GetReturnType(ctx, ct))
            .Where(static t => t is not null)
            .Select(static (t, _) => t!);

        var collected = mapCalls.Collect();

        context.RegisterSourceOutput(collected, GenerateSource);
    }

    private static bool IsInvocationCandidate(SyntaxNode node)
    {
        if (node is InvocationExpressionSyntax invocation &&
            invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            (memberAccess.Name.Identifier.Text == "MapGet" ||
             memberAccess.Name.Identifier.Text == "MapPost" ||
             memberAccess.Name.Identifier.Text == "MapPut" ||
             memberAccess.Name.Identifier.Text == "MapDelete"))
        {
            return true;
        }
        return false;
    }

    private static string? GetReturnType(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        var semanticModel = ctx.SemanticModel;

        // Get the last argument (lambda or method reference)
        var lastArg = invocation.ArgumentList.Arguments.LastOrDefault();
        if (lastArg == null)
            return null;

        // Try to get the type from the lambda or delegate
        var typeInfo = semanticModel.GetTypeInfo(lastArg.Expression, ct);
        if (typeInfo.ConvertedType == null)
            return null;

        // If it's a lambda, find the return type
        if (lastArg.Expression is LambdaExpressionSyntax lambda)
        {
            // Walk the lambda body to find the return type
            if (lambda is SimpleLambdaExpressionSyntax simpleLambda)
            {
                var bodyType = semanticModel.GetTypeInfo(simpleLambda.Body, ct);
                if (bodyType.ConvertedType != null)
                {
                    var namedType = bodyType.ConvertedType as INamedTypeSymbol;
                    if (namedType != null && namedType.Name == "ValueTask")
                    {
                        // Extract T from ValueTask<T>
                        if (namedType.TypeArguments.Length == 1)
                            return GetResultTypeName(namedType.TypeArguments[0]);
                    }
                    return GetResultTypeName(bodyType.ConvertedType);
                }
            }
            else if (lambda is ParenthesizedLambdaExpressionSyntax parenLambda)
            {
                var bodyType = semanticModel.GetTypeInfo(parenLambda.Body, ct);
                if (bodyType.ConvertedType != null)
                {
                    var namedType = bodyType.ConvertedType as INamedTypeSymbol;
                    if (namedType != null && namedType.Name == "ValueTask")
                    {
                        if (namedType.TypeArguments.Length == 1)
                            return GetResultTypeName(namedType.TypeArguments[0]);
                    }
                    return GetResultTypeName(bodyType.ConvertedType);
                }
            }
        }

        return null;
    }

    private static string? GetResultTypeName(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named)
        {
            var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (!fullName.StartsWith("global::System"))
                return fullName;
        }
        return null;
    }

    private static void GenerateSource(SourceProductionContext context, ImmutableArray<string> types)
    {
        if (types.Length == 0)
            return;

        var seen = new HashSet<string>();
        var sb = new StringBuilder();

        var idx = 0;
        foreach (var type in types)
        {
            if (type != null && seen.Add(type))
            {
                sb.AppendLine("[PicoJetson.PicoJsonSerializable(typeof(" + type + "))]");
                sb.AppendLine("file sealed class __PicoJetson_Serializable_" + idx + " { }");
                idx++;
            }
        }

        if (sb.Length > 0)
            context.AddSource("MapMethod_Serializables.g.cs", sb.ToString());
    }
}
