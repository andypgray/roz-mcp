using Microsoft.CodeAnalysis;
using Zphil.Roz.Enums;

namespace Zphil.Roz.Models;

internal sealed record FindSymbolResult(
    string SearchName,
    List<ISymbol> Symbols,
    string SolutionDir,
    int Depth,
    int TotalCount,
    SymbolicKind? Kind = null,
    string? ExcludePattern = null,
    string? ContainingType = null,
    string? Project = null,
    SymbolMatchMode MatchMode = SymbolMatchMode.Contains,
    bool IncludeBody = false,
    IReadOnlyList<ProjectDistributionEntry>? Distribution = null,
    List<string>? Suggestions = null,
    SymbolicKind[]? MemberKinds = null,
    bool ContainingTypeIsNamespace = false,
    int? MaxMembers = null,
    string[]? FilePaths = null,
    int ExcludedTestProjectCount = 0,
    int IncludedTestCount = 0,
    int? Arity = null,
    SymbolicKind[]? FilteredOutKinds = null);

internal sealed record SymbolsOverviewResult(
    string RelPath,
    List<ISymbol> Symbols,
    string SolutionDir,
    int Depth,
    string? Error = null,
    bool HasTopLevelStatements = false,
    string? AbsolutePath = null,
    SymbolicKind[]? MemberKinds = null,
    int? MaxMembers = null,
    int TotalTypeCount = 0);

internal sealed record SymbolAtPositionResult(
    ISymbol Symbol,
    string SolutionDir,
    int FormatDepth,
    bool IncludeBody,
    bool IsAtDeclaration = false,
    string? ProjectOrAssemblyName = null,
    int? MaxMembers = null);

internal sealed record MultiFileOverviewResult(
    SymbolsOverviewResult[] Results,
    int GlobalTotalTypes,
    int FileCount);

internal sealed record FindOverloadsResult(
    string SymbolName,
    string ContainingTypeName,
    List<ISymbol> Overloads,
    string SolutionDir);
