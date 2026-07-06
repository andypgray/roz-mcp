using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Converts Roslyn symbols and diagnostics into human-readable plain text
///     optimized for LLM consumption.
/// </summary>
internal static class SymbolFormatter
{
    private const int CompactSignatureParamThreshold = 3;

    private static readonly string[] MemberKindOrder =
        ["field", "constructor", "property", "indexer", "method", "operator", "destructor", "event", "nested type"];

    /// <summary>
    ///     Formats a single symbol with its declaration signature, location, and optionally its members.
    /// </summary>
    /// <example>
    ///     <b>depth=0 (default):</b>
    ///     <code>
    ///     public class Circle : Shape
    ///       Location: TestFixture/Shapes/Circle.cs:3
    ///     </code>
    ///     <b>depth=1 (with members):</b>
    ///     <code>
    ///     public abstract class Shape : IShape
    ///       Location: TestFixture/Shapes/Shape.cs:3
    ///       Members (3):
    ///         [public abstract property] double Area  :7
    ///         [public abstract property] double Perimeter  :10
    ///         [public virtual method] string Describe()  :13
    ///     </code>
    ///     <b>A method symbol (depth=0):</b>
    ///     <code>
    ///     public method string ProcessShape(IShape shape)
    ///       Location: TestFixture/Services/ShapeService.cs:5
    ///       Containing type: ShapeService
    ///     </code>
    /// </example>
    public static string FormatSymbol(
        ISymbol symbol, string solutionDir, int depth = 0, bool includeBody = false, bool inlineLocation = false,
        string? filterToFilePath = null, string? projectOrAssemblyName = null, bool includeDocs = false,
        SymbolicKind[]? memberKinds = null, int? maxMembers = null, int? maxBodyLines = null,
        Location[]? locationOverride = null, bool suppressContainingType = false, bool includeGenerated = false)
    {
        var sb = new StringBuilder();

        // Declaration signature
        string attributesPrefix = FormatAttributes(symbol);
        string declarationPrefix = FormatMemberTag(symbol);
        string signature = GetSignature(symbol);

        sb.Append(attributesPrefix);
        sb.Append(declarationPrefix);
        sb.Append(' ');
        sb.Append(signature);

        // Location(s)
        Location[] sourceLocations = locationOverride
                                     ?? SymbolMerger.CollectSourceLocations(symbol.Locations, includeGenerated);
        // Whether the container declaration spans more than one source file (a partial type). Derived from
        // the header's own location array so member-row suffixes always agree with the header rendered above.
        bool containerSpansMultipleFiles = sourceLocations.Length > 1;

        if (inlineLocation)
        {
            if (sourceLocations.Length > 0)
            {
                Location chosen = ChooseInlineLocation(sourceLocations, filterToFilePath);
                int line = chosen.GetLineSpan().StartLinePosition.Line + 1;
                sb.Append($"  :{line}");
            }

            sb.AppendLine();
        }
        else if (symbol is INamespaceSymbol && sourceLocations.Length > 1)
        {
            // Namespace summary: show file count and project names instead of listing every file
            sb.AppendLine();
            string[] projectNames = sourceLocations
                .Select(l => GetProjectDirectoryName(l, solutionDir))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            int fileCount = sourceLocations.Length;
            sb.AppendLine($"  Spans {projectNames.Length} {Pluralize("project", projectNames.Length)}, {fileCount} files");
            if (projectNames.Length > 0)
            {
                sb.AppendLine($"  Projects: {String.Join(", ", projectNames)}");
            }
        }
        else
        {
            sb.AppendLine();
            if (sourceLocations.Length > 1)
            {
                sb.AppendLine($"  Locations ({sourceLocations.Length}):");
                foreach (Location loc in sourceLocations)
                {
                    sb.AppendLine($"    {loc.ToFileLineString(solutionDir)}");
                }
            }
            else if (sourceLocations.Length == 1)
            {
                sb.AppendLine($"  Location: {sourceLocations[0].ToFileLineString(solutionDir)}");
            }
        }

        // Documentation
        if (includeDocs)
        {
            XmlDocFormatter.AppendDocumentation(sb, symbol, "  ");
        }

        // Project or assembly
        if (projectOrAssemblyName is not null)
        {
            bool isMetadataOnly = symbol.IsMetadataSymbol();
            string label = isMetadataOnly ? "Assembly" : "Project";
            sb.AppendLine($"  {label}: {projectOrAssemblyName}");
        }

        // Kind and containing type (suppressed when all results share the same type, e.g. containingType filter)
        if (symbol.ContainingType is not null)
        {
            if (!suppressContainingType)
            {
                sb.AppendLine($"  Containing type: {symbol.ContainingType.Name}");
            }
        }
        else if (symbol.ContainingNamespace is { IsGlobalNamespace: false } ns)
        {
            sb.AppendLine($"  Namespace: {ns.ToDisplayString()}");
        }

        // Type-specific details
        AppendTypeSpecificDetails(sb, symbol);

        // When showing body for a type, suppress member listing to avoid redundancy
        if (includeBody && symbol is INamedTypeSymbol)
        {
            depth = 0;
        }

        // Member count summary (depth 0 only, for types with members)
        if (depth == 0 && symbol is INamedTypeSymbol countType
                       && countType.TypeKind is not TypeKind.Enum and not TypeKind.Delegate)
        {
            List<ISymbol> visibleMembers = GetVisibleMembers(countType, filterToFilePath, memberKinds);

            if (visibleMembers.Count > 0)
            {
                sb.AppendLine(FormatMemberCountSummary(visibleMembers));
            }
        }

        // Members (if depth > 0 and this is a type)
        if (depth > 0 && symbol is INamedTypeSymbol namedType)
        {
            List<ISymbol> members = GetVisibleMembers(namedType, filterToFilePath, memberKinds);

            if (members.Count > 0)
            {
                (List<ISymbol> displayMembers, int totalMembers) = members.Truncate(maxMembers);

                sb.AppendLine($"  Members ({totalMembers}):");
                foreach (ISymbol member in displayMembers)
                {
                    string memberTag = FormatMemberTag(member);
                    string memberSig = GetCompactSignature(member);
                    string lineSuffix = FormatMemberLineSuffix(member, filterToFilePath, containerSpansMultipleFiles);
                    string? memberDoc = includeDocs ? XmlDocFormatter.FormatSummaryOnly(member) : null;
                    string docSuffix = memberDoc is not null ? $" \u2014 {memberDoc}" : "";
                    sb.AppendLine($"    [{memberTag}] {memberSig}{lineSuffix}{docSuffix}");

                    if (depth > 1 && member is INamedTypeSymbol nestedType
                                  && (memberKinds is null || memberKinds.Length == 0))
                    {
                        List<ISymbol> nestedMembers = GetVisibleMembers(nestedType, filterToFilePath, memberKinds);
                        foreach (ISymbol nested in nestedMembers)
                        {
                            string nestedSuffix = FormatMemberLineSuffix(nested, filterToFilePath, containerSpansMultipleFiles);
                            sb.AppendLine($"      [{FormatMemberTag(nested)}] {GetCompactSignature(nested)}{nestedSuffix}");
                        }
                    }
                }

                if (displayMembers.Count < totalMembers)
                {
                    int hiddenCount = totalMembers - displayMembers.Count;
                    string breakdown = FormatKindBreakdown(members.Skip(displayMembers.Count));
                    sb.AppendLine($"    ... and {hiddenCount} more ({breakdown} — filter with memberKinds or increase maxMembers)");
                }
            }
        }

        // Body (source code)
        if (includeBody)
        {
            string? body = GetSymbolBody(symbol);
            if (body is not null)
            {
                string[] bodyLines = body.Split('\n');
                int totalLines = bodyLines.Length;
                int cap = maxBodyLines.GetValueOrDefault(totalLines);
                bool truncated = totalLines > cap;
                string[] dedentedBody = TextDedenter.Dedent(truncated ? bodyLines[..cap] : bodyLines);
                sb.AppendLine("  Body:");
                foreach (string line in dedentedBody)
                {
                    sb.Append("    ");
                    sb.AppendLine(line.TrimEnd('\r'));
                }

                if (truncated)
                {
                    sb.AppendLine($"    (body truncated at {cap} lines, {totalLines} total)");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Formats a list of symbols as a numbered list.
    /// </summary>
    /// <example>
    ///     <code>
    ///     1. public class Circle : Shape
    ///       Location: TestFixture/Shapes/Circle.cs:3
    /// 
    ///     2. public class Rectangle : Shape
    ///       Location: TestFixture/Shapes/Rectangle.cs:3
    ///     </code>
    ///     Returns <c>"No symbols found."</c> when the list is empty.
    /// </example>
    public static string FormatSymbolList(
        IEnumerable<ISymbol> symbols, string solutionDir, int depth = 0, bool includeBody = false,
        bool includeDocs = false, SymbolicKind[]? memberKinds = null, int? maxMembers = null,
        int? maxBodyLines = null, bool suppressContainingType = false, bool includeGenerated = false) =>
        FormatSymbolList(SymbolMerger.MergeNamespaceSymbols(symbols, includeGenerated), solutionDir, depth, includeBody, includeDocs,
            memberKinds, maxMembers, maxBodyLines, suppressContainingType, includeGenerated);

    /// <summary>
    ///     Formats a pre-merged symbol list, where namespace duplicates have already been collapsed
    ///     by <see cref="SymbolMerger.MergeNamespaceSymbols" />.
    /// </summary>
    public static string FormatSymbolList(
        List<(ISymbol Symbol, Location[]? LocationOverride)> mergedSymbols, string solutionDir, int depth = 0,
        bool includeBody = false, bool includeDocs = false, SymbolicKind[]? memberKinds = null,
        int? maxMembers = null, int? maxBodyLines = null, bool suppressContainingType = false,
        bool includeGenerated = false)
    {
        var sb = new StringBuilder();
        var index = 1;

        foreach ((ISymbol symbol, Location[]? locationOverride) in mergedSymbols)
        {
            sb.Append($"{index}. ");
            sb.AppendLine(FormatSymbol(symbol, solutionDir, depth, includeBody, includeDocs: includeDocs,
                memberKinds: memberKinds, maxMembers: maxMembers, maxBodyLines: maxBodyLines,
                locationOverride: locationOverride, suppressContainingType: suppressContainingType,
                includeGenerated: includeGenerated));
            sb.AppendLine();
            index++;
        }

        return index == 1
            ? "No symbols found."
            : sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Formats a symbol as just its name and source location, for the <see cref="DetailLevel.Minimal" /> level.
    /// </summary>
    /// <example>
    ///     <code>Circle (TestFixture/Shapes/Circle.cs:3)</code>
    /// </example>
    internal static string FormatSymbolNameOnly(ISymbol symbol, string solutionDir)
    {
        Location? loc = symbol.PreferNonGeneratedSourceLocation();
        string location = loc is not null ? $" ({loc.ToFileLineString(solutionDir)})" : "";
        return $"{symbol.Name}{location}";
    }

    private static bool MatchesMemberKinds(ISymbol member, SymbolicKind[]? memberKinds)
    {
        if (memberKinds is null || memberKinds.Length == 0)
        {
            return true;
        }

        return Array.Exists(memberKinds, k => member.MatchesKindFilter(k));
    }

    private static bool IsDeclaredInFile(ISymbol member, string? filePath)
    {
        if (filePath is null)
        {
            return true;
        }

        return member.DeclaringSyntaxReferences
            .Any(r => String.Equals(r.SyntaxTree.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
    }

    private static List<ISymbol> GetVisibleMembers(
        INamedTypeSymbol type, string? filterToFilePath, SymbolicKind[]? memberKinds)
    {
        return type.GetMembers()
            .Where(m => m.IsUserVisibleMember()
                        && IsDeclaredInFile(m, filterToFilePath)
                        && MatchesMemberKinds(m, memberKinds))
            .ToList();
    }

    private static string FormatMemberCountSummary(List<ISymbol> members)
    {
        int total = members.Count;
        string breakdown = FormatKindBreakdown(members);
        return $"  {total} {Pluralize("member", total)} ({breakdown})";
    }

    private static string FormatKindBreakdown(IEnumerable<ISymbol> members)
    {
        Dictionary<string, int> counts = members
            .GroupBy(m => m is INamedTypeSymbol ? "nested type" : m.GetKindString())
            .ToDictionary(g => g.Key, g => g.Count());

        List<string> parts = new();
        foreach (string kind in MemberKindOrder)
        {
            if (counts.TryGetValue(kind, out int count))
            {
                parts.Add($"{count} {Pluralize(kind, count)}");
            }
        }

        // Catch any kinds not in the predefined order
        foreach (KeyValuePair<string, int> kvp in counts)
        {
            if (!MemberKindOrder.Contains(kvp.Key))
            {
                parts.Add($"{kvp.Value} {Pluralize(kvp.Key, kvp.Value)}");
            }
        }

        return String.Join(", ", parts);
    }

    private static string Pluralize(string kind, int count)
    {
        if (count == 1)
        {
            return kind;
        }

        return kind switch
        {
            "property" => "properties",
            "class" => "classes",
            "nested type" => "nested types",
            _ => kind + "s"
        };
    }

    private static string FormatAttributes(ISymbol symbol)
    {
        if (symbol is not INamedTypeSymbol { TypeKind: TypeKind.Enum } namedType)
        {
            return "";
        }

        bool hasFlags = namedType.GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute");
        return hasFlags ? "[Flags] " : "";
    }

    internal static string FormatMemberTag(ISymbol member)
    {
        // Skip kind for operators — "operator" is already part of the C# signature
        string kind = member is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion }
            ? ""
            : member.GetKindString();

        IEnumerable<string> tagParts = new[] { member.GetAccessibilityString(), member.GetModifiersString(), kind }
            .Where(s => s.Length > 0);
        return String.Join(" ", tagParts);
    }

    private static string FormatTypeParameters(ImmutableArray<ITypeParameterSymbol> typeParams)
    {
        if (typeParams.IsEmpty)
        {
            return "";
        }

        return $"<{String.Join(", ", typeParams.Select(tp => tp.Name))}>";
    }

    private static string FormatConstraints(ImmutableArray<ITypeParameterSymbol> typeParams)
    {
        if (typeParams.IsEmpty)
        {
            return "";
        }

        List<string> parts = new();
        foreach (ITypeParameterSymbol tp in typeParams)
        {
            List<string> constraints = new();

            if (tp.HasUnmanagedTypeConstraint)
            {
                constraints.Add("unmanaged");
            }
            else if (tp.HasValueTypeConstraint)
            {
                constraints.Add("struct");
            }
            else if (tp.HasReferenceTypeConstraint)
            {
                constraints.Add("class");
            }

            if (tp.HasNotNullConstraint)
            {
                constraints.Add("notnull");
            }

            foreach (ITypeSymbol ct in tp.ConstraintTypes)
            {
                constraints.Add(ct.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            }

            if (tp.HasConstructorConstraint)
            {
                constraints.Add("new()");
            }

            if (constraints.Count > 0)
            {
                parts.Add($"where {tp.Name} : {String.Join(", ", constraints)}");
            }
        }

        return parts.Count > 0 ? " " + String.Join(" ", parts) : "";
    }

    private static string GetSignature(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method => FormatMethodSignature(method),
            IPropertySymbol prop => prop.IsIndexer
                ? $"{prop.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} this[{FormatParameterList(prop.Parameters)}]"
                : $"{prop.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {prop.Name}",
            IFieldSymbol field => $"{field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {field.Name}",
            INamedTypeSymbol type => FormatTypeSignature(type),
            IEventSymbol evt => $"{evt.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {evt.Name}",
            INamespaceSymbol ns => ns.ToDisplayString(),
            _ => symbol.Name
        };
    }

    private static string FormatMethodSignature(IMethodSymbol method)
    {
        switch (method.MethodKind)
        {
            case MethodKind.Constructor or MethodKind.StaticConstructor:
                return $"{method.ContainingType.Name}({FormatParameterList(method.Parameters)})";

            case MethodKind.Destructor:
                return $"~{method.ContainingType.Name}()";

            case MethodKind.UserDefinedOperator:
            {
                string returnType = method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                string operatorToken = OperatorNames.GetDisplayToken(method.Name);
                return $"{returnType} operator {operatorToken}({FormatParameterList(method.Parameters)})";
            }

            case MethodKind.Conversion:
            {
                string returnType = method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                string conversionKind = method.Name == WellKnownMemberNames.ImplicitConversionName
                    ? "implicit"
                    : "explicit";
                return $"{conversionKind} operator {returnType}({FormatParameterList(method.Parameters)})";
            }

            default:
            {
                string retType = method.ReturnsVoid
                    ? "void"
                    : method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

                string typeParams = FormatTypeParameters(method.TypeParameters);
                string constraints = FormatConstraints(method.TypeParameters);

                return $"{retType} {method.Name}{typeParams}({FormatParameterList(method.Parameters)}){constraints}";
            }
        }
    }

    internal static string FormatParameterList(ImmutableArray<IParameterSymbol> parameters)
    {
        return String.Join(", ", parameters.Select(p =>
        {
            string thisModifier = IsExtensionMethodReceiver(p) ? "this " : "";
            string paramsModifier = p.IsParams ? "params " : "";
            string refModifier = p.RefKind switch
            {
                RefKind.Ref => "ref ",
                RefKind.Out => "out ",
                RefKind.In => "in ",
                RefKind.RefReadOnlyParameter => "ref readonly ",
                _ => ""
            };
            string type = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            string defaultValue = p.HasExplicitDefaultValue
                ? $" = {FormatDefaultValue(p.ExplicitDefaultValue)}"
                : "";
            return $"{thisModifier}{paramsModifier}{refModifier}{type} {p.Name}{defaultValue}";
        }));
    }

    // ReducedFrom: null → original declaration form (not the reduced call-site form
    // where the receiver has been elided). Only the declaration form needs "this ".
    private static bool IsExtensionMethodReceiver(IParameterSymbol p) =>
        p.Ordinal == 0 && p.ContainingSymbol is IMethodSymbol { IsExtensionMethod: true, ReducedFrom: null };

    private static string FormatDefaultValue(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        char c => $"'{c}'",
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null"
    };

    /// <summary>
    ///     Formats a member signature in compact form for member listings.
    ///     Constructors with more than <see cref="CompactSignatureParamThreshold" /> parameters
    ///     show <c>ClassName(Type1, Type2, ...)</c> (types only, no parameter names).
    ///     Ordinary methods with more than <see cref="CompactSignatureParamThreshold" /> parameters
    ///     show <c>ReturnType MethodName(Type1, Type2, ...)</c> (types only, no parameter names).
    ///     Operators, conversions, destructors, and methods with few parameters use full signatures.
    /// </summary>
    private static string GetCompactSignature(ISymbol symbol)
    {
        if (symbol is not IMethodSymbol method)
        {
            return GetSignature(symbol);
        }

        int paramCount = method.Parameters.Length;
        bool isConstructor = method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor;

        if (isConstructor && paramCount > CompactSignatureParamThreshold)
        {
            string paramTypes = FormatParameterTypeList(method.Parameters);
            return $"{method.ContainingType.Name}({paramTypes})";
        }

        if (!isConstructor && paramCount > CompactSignatureParamThreshold && method.MethodKind == MethodKind.Ordinary)
        {
            string retType = method.ReturnsVoid
                ? "void"
                : method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            string typeParams = FormatTypeParameters(method.TypeParameters);
            string paramTypes = FormatParameterTypeList(method.Parameters);
            return $"{retType} {method.Name}{typeParams}({paramTypes})";
        }

        return GetSignature(symbol);
    }

    /// <summary>
    ///     Formats parameter types only (no names) for compact signatures.
    ///     Example: <c>IType, ConversionFlags, int, string</c>
    /// </summary>
    private static string FormatParameterTypeList(ImmutableArray<IParameterSymbol> parameters)
    {
        return String.Join(", ", parameters.Select(p =>
        {
            string thisModifier = IsExtensionMethodReceiver(p) ? "this " : "";
            string paramsModifier = p.IsParams ? "params " : "";
            string refModifier = p.RefKind switch
            {
                RefKind.Ref => "ref ",
                RefKind.Out => "out ",
                RefKind.In => "in ",
                RefKind.RefReadOnlyParameter => "ref readonly ",
                _ => ""
            };
            return $"{thisModifier}{paramsModifier}{refModifier}{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}";
        }));
    }

    private static string FormatTypeSignature(INamedTypeSymbol type)
    {
        var sb = new StringBuilder(type.Name);
        sb.Append(FormatTypeParameters(type.TypeParameters));

        IMethodSymbol? primaryCtor = type.InstanceConstructors
            .FirstOrDefault(c => c.DeclaringSyntaxReferences
                .Any(r => r.GetSyntax() is TypeDeclarationSyntax));
        if (primaryCtor is not null && !primaryCtor.Parameters.IsEmpty)
        {
            sb.Append($"({FormatParameterList(primaryCtor.Parameters)})");
        }

        bool hasExplicitBase = type.BaseType is not null &&
                               type.BaseType.SpecialType != SpecialType.System_Object &&
                               type.BaseType.SpecialType != SpecialType.System_ValueType &&
                               type.BaseType.SpecialType != SpecialType.System_Enum &&
                               type.BaseType.SpecialType != SpecialType.System_MulticastDelegate;

        if (hasExplicitBase)
        {
            sb.Append($" : {type.BaseType!.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
        }

        INamedTypeSymbol[] interfaces = type.Interfaces
            .Where(i => !i.IsImplicitlyDeclared)
            .ToArray();

        if (interfaces.Length > 0)
        {
            sb.Append(hasExplicitBase ? ", " : " : ");
            sb.Append(String.Join(", ", interfaces.Select(i =>
                i.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))));
        }

        sb.Append(FormatConstraints(type.TypeParameters));

        return sb.ToString();
    }

    private static void AppendTypeSpecificDetails(StringBuilder sb, ISymbol symbol)
    {
        if (symbol is IMethodSymbol method)
        {
            if (method.IsAsync)
            {
                sb.AppendLine("  Async: true");
            }
        }
        else if (symbol is IPropertySymbol prop)
        {
            List<string> accessors = new();
            if (prop.GetMethod is not null)
            {
                accessors.Add("get");
            }

            if (prop.SetMethod is not null)
            {
                accessors.Add(prop.SetMethod.IsInitOnly ? "init" : "set");
            }

            sb.AppendLine($"  Accessors: {String.Join(", ", accessors)}");
        }
    }

    /// <summary>
    ///     Selects the best location for inline display. Prefers the location in the queried file,
    ///     then non-generated locations, then falls back to the first available.
    /// </summary>
    private static Location ChooseInlineLocation(Location[] sourceLocations, string? filterToFilePath)
    {
        if (sourceLocations.Length == 1)
        {
            return sourceLocations[0];
        }

        // Prefer the location in the file being queried
        if (filterToFilePath is not null)
        {
            Location? match = Array.Find(sourceLocations,
                l => String.Equals(l.SourceTree?.FilePath, filterToFilePath, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        // Prefer non-generated locations
        Location? nonGenerated = Array.Find(sourceLocations,
            l => !l.IsInGeneratedFile());

        return nonGenerated ?? sourceLocations[0];
    }

    /// <summary>
    ///     Builds the trailing location suffix for a member row in a depth&gt;0 listing, mirroring the
    ///     top-level <c>  :{line}</c> convention. Returns an empty string for a metadata member (no source
    ///     location) so BCL/NuGet member listings stay byte-identical. When the container declaration spans
    ///     multiple source files (a partial type shown without a file filter — the find_symbol /
    ///     go_to_definition path), the suffix carries the file name (<c>  {file}:{line}</c>) so rows from
    ///     different partials are distinguishable; the file-scoped overview path
    ///     (<paramref name="filterToFilePath" /> set) always gets the bare <c>  :{line}</c> because its
    ///     members are already confined to one file.
    /// </summary>
    private static string FormatMemberLineSuffix(
        ISymbol member, string? filterToFilePath, bool containerSpansMultipleFiles)
    {
        Location[] memberLocations = SymbolMerger.CollectSourceLocations(member.Locations, true);
        if (memberLocations.Length == 0)
        {
            return "";
        }

        Location chosen = ChooseInlineLocation(memberLocations, filterToFilePath);
        int line = chosen.GetLineSpan().StartLinePosition.Line + 1;

        if (containerSpansMultipleFiles && filterToFilePath is null && chosen.SourceTree?.FilePath is { } filePath)
        {
            return $"  {Path.GetFileName(filePath)}:{line}";
        }

        return $"  :{line}";
    }

    /// <summary>
    ///     Extracts the first directory segment of a source location relative to the solution directory,
    ///     which typically corresponds to the project directory name.
    /// </summary>
    private static string? GetProjectDirectoryName(Location location, string solutionDir)
    {
        string? filePath = location.SourceTree?.FilePath;
        if (filePath is null)
        {
            return null;
        }

        string relativePath = Path.GetRelativePath(solutionDir, filePath);
        int separatorIndex = relativePath.IndexOfAny(['/', '\\']);
        return separatorIndex > 0 ? relativePath[..separatorIndex] : null;
    }

    private static string? GetSymbolBody(ISymbol symbol)
    {
        SyntaxReference? syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef is null)
        {
            return null;
        }

        SyntaxNode node = syntaxRef.GetSyntax();

        // SyntaxNode.ToString() drops the node's leading trivia, so the first line loses its
        // source indentation while interior lines keep theirs. Without restoring it, Dedent
        // sees a 0-indent first line, early-returns, and the result staircases. Re-prepend the
        // whitespace between the start of the first physical line and the first token.
        SourceText sourceText = node.SyntaxTree.GetText();
        TextLine firstLine = sourceText.Lines.GetLineFromPosition(node.SpanStart);
        var indent = sourceText.ToString(TextSpan.FromBounds(firstLine.Start, node.SpanStart));
        return indent + node;
    }
}
