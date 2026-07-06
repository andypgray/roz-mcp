namespace Zphil.Roz.Enums;

/// <summary>
///     Selector for which kinds of references <c>get_unused_references</c> should analyze.
/// </summary>
internal enum UnusedReferencesKind
{
    /// <summary>Analyze <c>&lt;ProjectReference&gt;</c> entries only. Confident output.</summary>
    Projects,

    /// <summary>Analyze <c>&lt;PackageReference&gt;</c> entries only. Output framed as a weak signal.</summary>
    Packages,

    /// <summary>Analyze both project and package references in one report.</summary>
    All
}
