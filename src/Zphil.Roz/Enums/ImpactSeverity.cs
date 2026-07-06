namespace Zphil.Roz.Enums;

/// <summary>
///     Per-site verdict for a proposed change, produced by <c>analyze_change_impact</c>.
/// </summary>
internal enum ImpactSeverity
{
    /// <summary>The site still compiles unchanged after the proposed change.</summary>
    Compatible,

    /// <summary>The site compiles only after a manual edit (e.g. an added cast or a new argument).</summary>
    RequiresUpdate,

    /// <summary>
    ///     The site will not compile after the change, or compiles with silently changed meaning (an
    ///     argument re-binding to a different parameter, a call retargeting to another overload).
    /// </summary>
    Unsafe
}
