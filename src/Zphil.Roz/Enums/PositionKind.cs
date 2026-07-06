namespace Zphil.Roz.Enums;

/// <summary>
///     Categorizes what kind of syntax element is at a given position.
/// </summary>
internal enum PositionKind
{
    Unknown,
    Comment,
    DocComment,
    Whitespace,
    DisabledCode,
    StringLiteral,
    NumericLiteral,
    Keyword,
    Punctuation,
    Trivia
}
