using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Zphil.Roz.Symbols;

/// <summary>
///     Resolves a user-supplied type name (the <c>newType</c> of an <c>analyze_change_impact</c>
///     <c>TypeChange</c>) to an <see cref="ITypeSymbol" />, honouring the scope at the target
///     symbol's declaration.
/// </summary>
/// <remarks>
///     <see cref="Compilation.GetTypeByMetadataName" /> (used by <see cref="FqnResolver" />) cannot
///     bind C# keyword aliases (<c>long</c>) or generic syntax (<c>IReadOnlyList&lt;Order&gt;</c>),
///     and ignores the using-directive context of the declaration site. Speculative binding at the
///     target's declaration does all three: <c>using</c>s, aliases, keywords, and generics resolve
///     exactly as they would if the type were written there. The metadata fallback (FQN +
///     primitive keyword map) covers targets with no source declaration.
/// </remarks>
internal static class TypeNameResolver
{
    private static readonly IReadOnlyDictionary<string, SpecialType> PrimitiveKeywords =
        new Dictionary<string, SpecialType>(StringComparer.Ordinal)
        {
            ["bool"] = SpecialType.System_Boolean,
            ["byte"] = SpecialType.System_Byte,
            ["sbyte"] = SpecialType.System_SByte,
            ["char"] = SpecialType.System_Char,
            ["short"] = SpecialType.System_Int16,
            ["ushort"] = SpecialType.System_UInt16,
            ["int"] = SpecialType.System_Int32,
            ["uint"] = SpecialType.System_UInt32,
            ["long"] = SpecialType.System_Int64,
            ["ulong"] = SpecialType.System_UInt64,
            ["float"] = SpecialType.System_Single,
            ["double"] = SpecialType.System_Double,
            ["decimal"] = SpecialType.System_Decimal,
            ["string"] = SpecialType.System_String,
            ["object"] = SpecialType.System_Object
        };

    /// <summary>
    ///     Resolves <paramref name="newType" /> against the scope at <paramref name="target" />'s
    ///     declaration, throwing <see cref="UserErrorException" /> when it cannot be bound.
    /// </summary>
    public static async Task<ITypeSymbol> ResolveAsync(
        ISymbol target, string newType, Solution solution, CancellationToken ct)
    {
        string trimmed = newType.Trim();
        if (trimmed.Length == 0)
        {
            throw new UserErrorException("newType must not be empty for changeKind=TypeChange.");
        }

        TypeSyntax typeSyntax = SyntaxFactory.ParseTypeName(trimmed);
        ITypeSymbol? resolved = await BindTypeSyntaxAsync(target, typeSyntax, solution, ct);
        if (resolved is not null)
        {
            return resolved;
        }

        throw new UserErrorException(
            $"Could not resolve type '{newType}' for changeKind=TypeChange. Use a keyword (e.g. long), " +
            "a simple or fully-qualified type name, or generic syntax (e.g. IReadOnlyList<Order>).");
    }

    /// <summary>
    ///     Binds an already-parsed <paramref name="typeSyntax" /> at <paramref name="target" />'s
    ///     declaration scope (usings, aliases, keywords, generics), falling back to the metadata
    ///     resolver; returns null when it cannot be bound anywhere. Shared by the string-based
    ///     <see cref="ResolveAsync" /> and the <c>newSignature</c> parameter binder so the
    ///     speculative-binding logic lives in one place.
    /// </summary>
    public static async Task<ITypeSymbol?> BindTypeSyntaxAsync(
        ISymbol target, TypeSyntax typeSyntax, Solution solution, CancellationToken ct)
    {
        // Speculative binding at the declaration site honours usings, aliases, keywords, generics.
        SyntaxReference? declRef = target.DeclaringSyntaxReferences.FirstOrDefault();
        if (declRef is not null && solution.GetDocument(declRef.SyntaxTree) is { } document)
        {
            SemanticModel? model = await document.GetSemanticModelAsync(ct);
            if (model is not null)
            {
                SyntaxNode declNode = await declRef.GetSyntaxAsync(ct);
                TypeInfo info = model.GetSpeculativeTypeInfo(
                    declNode.SpanStart, typeSyntax, SpeculativeBindingOption.BindAsTypeOrNamespace);
                if (info.Type is { TypeKind: not TypeKind.Error } resolved)
                {
                    return resolved;
                }
            }
        }

        return await ResolveFromMetadataAsync(typeSyntax.ToString().Trim(), solution, ct);
    }

    /// <summary>
    ///     Metadata fallback for targets with no source declaration: primitive keyword → special
    ///     type, then <see cref="FqnResolver" /> across the solution's compilations.
    /// </summary>
    private static async Task<ITypeSymbol?> ResolveFromMetadataAsync(
        string typeName, Solution solution, CancellationToken ct)
    {
        if (PrimitiveKeywords.TryGetValue(typeName, out SpecialType specialType))
        {
            foreach (Project project in solution.Projects)
            {
                Compilation? compilation = await project.GetCompilationAsync(ct);
                if (compilation is not null)
                {
                    return compilation.GetSpecialType(specialType);
                }
            }
        }

        List<ISymbol> matches = await FqnResolver.ResolveAsync(solution.Projects, typeName, ct: ct);
        return matches.OfType<ITypeSymbol>().FirstOrDefault();
    }
}
