namespace Zphil.Roz.Enums;

/// <summary>
///     The syntactic role a single reference plays at its use site, as classified by
///     <see cref="Services.ReferenceKindClassifier.Classify" />. Shared by <c>find_references</c>
///     (kind filtering) and <c>analyze_change_impact</c> (producer/consumer value-flow direction).
/// </summary>
internal enum ReferenceRole
{
    /// <summary>The symbol's value is read (a producer of a value): a property/field read, a method group.</summary>
    Read,

    /// <summary>The symbol is written (a consumer of a value): assignment LHS, <c>out</c>/<c>ref</c>, <c>++</c>/<c>--</c>.</summary>
    Write,

    /// <summary>The symbol is invoked: a method call, constructor, or indexer access.</summary>
    Invocation
}
