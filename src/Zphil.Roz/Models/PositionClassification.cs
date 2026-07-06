using Zphil.Roz.Enums;

namespace Zphil.Roz.Models;

/// <summary>
///     Result of classifying a position in a syntax tree: the kind and a human-readable description.
/// </summary>
internal readonly record struct PositionClassification(PositionKind Kind, string Description)
{
    internal bool IsComment => Kind is PositionKind.Comment or PositionKind.DocComment;

    /// <summary>
    ///     Returns true if the position is on non-symbol trivia (comments, preprocessor directives, disabled code).
    /// </summary>
    internal bool IsNonSymbolTrivia => IsComment || Kind == PositionKind.Trivia;
}
