namespace Zphil.Roz.Enums;

/// <summary>
///     Target accessibility for <c>analyze_change_impact</c>'s <c>AccessibilityNarrow</c> change.
/// </summary>
/// <remarks>
///     Clearer schema names than Roslyn's <see cref="Microsoft.CodeAnalysis.Accessibility" /> values
///     (whose <c>ProtectedOrInternal</c>/<c>ProtectedAndInternal</c> spellings invert the intuitive
///     reading). Listed roughly widest → narrowest, but narrowing is only a partial order
///     (<c>internal</c> and <c>protected</c> are mutually incomparable), so the strict-narrowing
///     check is an explicit lattice rather than a numeric comparison.
/// </remarks>
internal enum AccessibilityLevel
{
    /// <summary><c>public</c> — accessible everywhere.</summary>
    Public,

    /// <summary><c>protected internal</c> — same assembly OR derived type.</summary>
    ProtectedInternal,

    /// <summary><c>internal</c> — same assembly.</summary>
    Internal,

    /// <summary><c>protected</c> — declaring type or a derived type.</summary>
    Protected,

    /// <summary><c>private protected</c> — same assembly AND derived type.</summary>
    PrivateProtected,

    /// <summary><c>private</c> — declaring type only.</summary>
    Private
}
