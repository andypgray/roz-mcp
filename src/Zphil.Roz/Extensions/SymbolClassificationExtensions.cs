using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Zphil.Roz.Enums;

namespace Zphil.Roz.Extensions;

/// <summary>
///     Symbol classification: kind strings, modifiers, accessibility, visibility filtering.
/// </summary>
internal static class SymbolClassificationExtensions
{
    /// <summary>
    ///     Returns true if the symbol has no source declarations in the solution
    ///     (i.e., it comes from a metadata/framework assembly).
    /// </summary>
    public static bool IsMetadataSymbol(this ISymbol symbol) =>
        symbol.DeclaringSyntaxReferences.IsEmpty;

    /// <summary>
    ///     Returns true if the symbol is unreachable from code outside its declaring file,
    ///     so a textual scan for cross-file stray references is pure noise.
    ///     Covers locals, parameters, range variables, type parameters, local functions,
    ///     effectively-private members (any private link in the containing-type chain), and
    ///     file-scoped types. <c>internal</c> is deliberately NOT treated as local: unloaded
    ///     projects using <c>InternalsVisibleTo</c> are exactly the failure mode this scan exists for.
    /// </summary>
    public static bool IsLocallyScopedSymbol(this ISymbol symbol)
    {
        if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol or ITypeParameterSymbol)
        {
            return true;
        }

        if (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction })
        {
            return true;
        }

        for (ISymbol? current = symbol; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility == Accessibility.Private)
            {
                return true;
            }
        }

        for (INamedTypeSymbol? type = symbol as INamedTypeSymbol ?? symbol.ContainingType; type is not null; type = type.ContainingType)
        {
            foreach (SyntaxReference syntaxRef in type.DeclaringSyntaxReferences)
            {
                if (syntaxRef.GetSyntax() is TypeDeclarationSyntax { Modifiers: var modifiers } &&
                    modifiers.Any(SyntaxKind.FileKeyword))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Returns true if the symbol is an enum member field (implicitly public, static).
    /// </summary>
    private static bool IsEnumMember(this ISymbol symbol) =>
        symbol is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum };

    /// <summary>
    ///     Gets the accessibility keyword string for a symbol.
    /// </summary>
    public static string GetAccessibilityString(this ISymbol symbol)
    {
        // Enum members are always implicitly public — suppress the modifier for cleaner output.
        if (symbol.IsEnumMember())
        {
            return "";
        }

        return symbol.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.Private => "private",
            _ => ""
        };
    }

    /// <summary>
    ///     Returns true if the method symbol is an explicit constructor (not a primary or compiler-generated constructor).
    ///     Primary constructors (C# 12) have their declaring syntax on the type declaration, not a
    ///     ConstructorDeclarationSyntax.
    /// </summary>
    private static bool IsExplicitConstructor(this IMethodSymbol method)
    {
        if (method.MethodKind is not (MethodKind.Constructor or MethodKind.StaticConstructor))
        {
            return false;
        }

        return method.DeclaringSyntaxReferences
            .Any(r => r.GetSyntax() is ConstructorDeclarationSyntax);
    }

    /// <summary>
    ///     Gets a short kind description for a symbol (e.g., "class", "method", "property").
    /// </summary>
    public static string GetKindString(this ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol t => t.TypeKind switch
            {
                TypeKind.Class when t.IsRecord => "record class",
                TypeKind.Class => "class",
                TypeKind.Interface => "interface",
                TypeKind.Struct when t.IsRecord => "record struct",
                TypeKind.Struct when t.IsRefLikeType => "ref struct",
                TypeKind.Struct => "struct",
                TypeKind.Enum => "enum",
                TypeKind.Delegate => "delegate",
                _ => "type"
            },
            IMethodSymbol m => m.MethodKind switch
            {
                MethodKind.Constructor or MethodKind.StaticConstructor => "constructor",
                MethodKind.Destructor => "destructor",
                MethodKind.UserDefinedOperator or MethodKind.Conversion => "operator",
                _ => "method"
            },
            IPropertySymbol p => p.IsIndexer ? "indexer" : "property",
            IFieldSymbol f => f.IsEnumMember() ? "enum value" : "field",
            IEventSymbol => "event",
            INamespaceSymbol => "namespace",
            IParameterSymbol => "parameter",
            ILocalSymbol => "local",
            _ => symbol.Kind.ToString().ToLowerInvariant()
        };
    }

    /// <summary>
    ///     Gets the modifier keywords for a symbol (e.g., "static", "abstract", "sealed partial").
    /// </summary>
    public static string GetModifiersString(this ISymbol symbol)
    {
        List<string> modifiers = new(3);

        if (symbol.IsStatic && !symbol.IsEnumMember())
        {
            modifiers.Add("static");
        }

        if (symbol.IsAbstract && symbol is not INamedTypeSymbol { TypeKind: TypeKind.Interface })
        {
            modifiers.Add("abstract");
        }

        if (symbol.IsSealed && symbol is INamedTypeSymbol { TypeKind: not TypeKind.Enum and not TypeKind.Struct })
        {
            modifiers.Add("sealed");
        }

        if (symbol.IsVirtual)
        {
            modifiers.Add("virtual");
        }

        if (symbol.IsOverride)
        {
            modifiers.Add("override");
        }

        if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Struct, IsReadOnly: true })
        {
            modifiers.Add("readonly");
        }

        if (symbol is INamedTypeSymbol namedType)
        {
            foreach (SyntaxReference syntaxRef in namedType.DeclaringSyntaxReferences)
            {
                if (syntaxRef.GetSyntax() is TypeDeclarationSyntax typeDecl &&
                    typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    modifiers.Add("partial");
                    break;
                }
            }
        }

        return String.Join(" ", modifiers);
    }

    /// <summary>
    ///     Returns true if the member should be shown in member listings.
    ///     Includes regular named members, explicit constructors, destructors,
    ///     operators, indexers, and explicit interface implementations, but
    ///     excludes compiler-generated members and primary constructors.
    /// </summary>
    public static bool IsUserVisibleMember(this ISymbol member)
    {
        if (member.IsImplicitlyDeclared)
        {
            return false;
        }

        if (member.CanBeReferencedByName)
        {
            return true;
        }

        // Special members that are user-written but not referenceable by name
        return member switch
        {
            IMethodSymbol ms => ms.IsExplicitConstructor() ||
                                ms.MethodKind is MethodKind.Destructor or
                                    MethodKind.UserDefinedOperator or
                                    MethodKind.Conversion or
                                    MethodKind.ExplicitInterfaceImplementation,
            IPropertySymbol ps => ps.IsIndexer || ps.ExplicitInterfaceImplementations.Length > 0,
            IEventSymbol es => es.ExplicitInterfaceImplementations.Length > 0,
            _ => false
        };
    }

    /// <summary>
    ///     Returns true if the symbol matches the specified kind filter.
    /// </summary>
    public static bool MatchesKindFilter(this ISymbol symbol, SymbolicKind? kindFilter)
    {
        if (kindFilter is null)
        {
            return true;
        }

        return kindFilter.Value switch
        {
            SymbolicKind.Class => symbol is INamedTypeSymbol { TypeKind: TypeKind.Class },
            SymbolicKind.Interface => symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface },
            SymbolicKind.Struct => symbol is INamedTypeSymbol { TypeKind: TypeKind.Struct },
            SymbolicKind.Enum => symbol is INamedTypeSymbol { TypeKind: TypeKind.Enum },
            SymbolicKind.Delegate => symbol is INamedTypeSymbol { TypeKind: TypeKind.Delegate },
            SymbolicKind.Method => symbol is IMethodSymbol { MethodKind: not (MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor or MethodKind.UserDefinedOperator or MethodKind.Conversion) },
            SymbolicKind.Constructor => symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor },
            SymbolicKind.Property => symbol is IPropertySymbol { IsIndexer: false },
            SymbolicKind.Field => symbol is IFieldSymbol,
            SymbolicKind.Event => symbol is IEventSymbol,
            SymbolicKind.Namespace => symbol is INamespaceSymbol,
            SymbolicKind.Indexer => symbol is IPropertySymbol { IsIndexer: true },
            SymbolicKind.Operator => symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion },
            SymbolicKind.Destructor => symbol is IMethodSymbol { MethodKind: MethodKind.Destructor },
            _ => false
        };
    }

    /// <summary>
    ///     Maps a symbol back to its <see cref="SymbolicKind" />, or null if the symbol
    ///     doesn't correspond to any user-facing kind. Inverse of <see cref="MatchesKindFilter" />.
    /// </summary>
    public static SymbolicKind? ToSymbolicKind(this ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => SymbolicKind.Namespace,
        INamedTypeSymbol { TypeKind: TypeKind.Class } => SymbolicKind.Class,
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => SymbolicKind.Interface,
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => SymbolicKind.Struct,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => SymbolicKind.Enum,
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => SymbolicKind.Delegate,
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => SymbolicKind.Constructor,
        IMethodSymbol { MethodKind: MethodKind.Destructor } => SymbolicKind.Destructor,
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } => SymbolicKind.Operator,
        IMethodSymbol => SymbolicKind.Method,
        IPropertySymbol { IsIndexer: true } => SymbolicKind.Indexer,
        IPropertySymbol => SymbolicKind.Property,
        IFieldSymbol => SymbolicKind.Field,
        IEventSymbol => SymbolicKind.Event,
        _ => null
    };

    /// <summary>
    ///     Converts a <see cref="SymbolicKind" /> filter into a predicate and description string
    ///     suitable for <see cref="Symbols.SymbolResolver" /> matchFilter parameters.
    /// </summary>
    internal static (Func<ISymbol, bool>? Filter, string? Description) BuildKindFilter(this SymbolicKind? kind) =>
        kind.HasValue
            ? (s => s.MatchesKindFilter(kind), kind.Value.ToString().ToLowerInvariant())
            : (null, null);

    /// <summary>
    ///     Returns the immediate base member that <paramref name="symbol" /> overrides, or null if it doesn't override
    ///     anything.
    ///     Only handles the <c>override</c> keyword case (methods, properties, events) — not explicit/implicit interface
    ///     implementations.
    /// </summary>
    internal static ISymbol? GetOverriddenMember(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.OverriddenMethod,
        IPropertySymbol p => p.OverriddenProperty,
        IEventSymbol e => e.OverriddenEvent,
        _ => null
    };
}
