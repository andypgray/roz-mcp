using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Zphil.Roz.Enums;

namespace Zphil.Roz.Models;

/// <summary>
///     Shared base for reference search results (<see cref="FindReferencesResult" /> and
///     <see cref="FindCallersResult" />) so batch fan-out and formatter dispatch can share
///     a single generic parameter.
/// </summary>
internal abstract record ReferenceSearchResult(string SymbolName, SymbolQualifiers Qualifiers, string SolutionDir, int TotalCount);

internal sealed record ReferenceLocationWithContext(
    ReferenceLocation Loc,
    string[] Lines,
    int StartLineNumber,
    string? ProjectName = null);

/// <summary>
///     Per-project distribution of references or callers, computed before truncation.
/// </summary>
internal sealed record ProjectDistributionEntry(string ProjectName, int ReferenceCount, int FileCount);

/// <summary>
///     Per-file distribution of references or callers, computed before truncation.
/// </summary>
internal sealed record FileDistributionEntry(string RelativePath, string ProjectName, int ReferenceCount);

/// <summary>
///     A DI registration site detected via semantic analysis of DI container calls.
/// </summary>
internal sealed record DiRegistration(
    string Lifetime,
    string FilePath,
    int Line,
    string LineText,
    string? ProjectName,
    string ContainerName);

internal sealed record FindReferencesResult(
    string SymbolName,
    SymbolQualifiers Qualifiers,
    List<ReferenceLocationWithContext> Locations,
    string SolutionDir,
    int TotalCount,
    ReferenceKind Kinds = ReferenceKind.All,
    IReadOnlyList<ProjectDistributionEntry>? Distribution = null,
    IReadOnlyList<FileDistributionEntry>? FileDistribution = null,
    int ExcludedTestCount = 0,
    int IncludedTestCount = 0,
    IReadOnlyList<DiRegistration>? DiRegistrations = null,
    bool ProjectIgnored = false)
    : ReferenceSearchResult(SymbolName, Qualifiers, SolutionDir, TotalCount);

internal sealed record FindImplementationsResult(
    string SymbolName,
    SymbolQualifiers Qualifiers,
    List<ISymbol> Implementations,
    string SolutionDir,
    int TotalCount,
    IReadOnlyList<ProjectDistributionEntry>? Distribution = null,
    ISymbol? TargetSymbol = null,
    int ExcludedTestCount = 0,
    int IncludedTestCount = 0,
    int ExcludedMetadataCount = 0,
    IReadOnlyDictionary<string, IReadOnlyList<DiRegistration>>? DiRegistrationsByType = null);

internal sealed record LocationWithContext(
    Location Loc,
    string[] Lines,
    int StartLineNumber);

internal sealed record CallerWithLineText(
    ISymbol CallingSymbol,
    List<LocationWithContext> LocationsWithContext,
    string? ProjectName = null);

/// <summary>
///     Lightweight descriptor of an interface member that a concrete method/property implements.
/// </summary>
/// <remarks>
///     Used to signpost interface-dispatch callers when <c>find_references referenceKinds=invocations</c> is invoked
///     on the concrete member. <see cref="FilePath" /> and <see cref="Line" /> are <c>null</c> for
///     metadata-only interfaces (BCL, NuGet) that have no source location.
/// </remarks>
internal sealed record InterfaceMemberDescriptor(
    string ContainingTypeShort,
    string ContainingTypeFullName,
    string MemberName,
    string? FilePath,
    int? Line);

internal sealed record FindCallersResult(
    string SymbolName,
    SymbolQualifiers Qualifiers,
    List<CallerWithLineText> Callers,
    string SolutionDir,
    int TotalCount,
    IReadOnlyList<ProjectDistributionEntry>? Distribution = null,
    IReadOnlyList<FileDistributionEntry>? FileDistribution = null,
    int ExcludedTestCount = 0,
    int IncludedTestCount = 0,
    IReadOnlyList<DiRegistration>? DiRegistrations = null,
    int OverloadCount = 0,
    IReadOnlyList<InterfaceMemberDescriptor>? ImplementedInterfaceMembers = null,
    string? ConcreteContainingTypeShort = null,
    bool ProjectIgnored = false)
    : ReferenceSearchResult(SymbolName, Qualifiers, SolutionDir, TotalCount);
