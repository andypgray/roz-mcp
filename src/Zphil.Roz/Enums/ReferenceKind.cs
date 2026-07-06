namespace Zphil.Roz.Enums;

/// <summary>
///     Filter for <c>find_references</c> results. Narrows the raw Roslyn reference list to
///     invocation sites, reads, or writes.
/// </summary>
internal enum ReferenceKind
{
    All,
    Invocations,
    Reads,
    Writes
}
