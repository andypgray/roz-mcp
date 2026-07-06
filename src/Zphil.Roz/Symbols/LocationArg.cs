namespace Zphil.Roz.Symbols;

/// <summary>
///     A parsed <c>location</c> tool argument: a file path with optional 1-based line and
///     column. Subtypes encode which fields are present so per-tool parse calls return the
///     exact shape required and validators are not needed at the call site (LSP's
///     <c>TextDocumentPositionParams</c> is the prior art).
/// </summary>
/// <remarks>
///     Named <c>LocationArg</c> rather than <c>Location</c> to avoid clashing with
///     <see cref="Microsoft.CodeAnalysis.Location" />, which is in scope across most files
///     that work with Roslyn symbols.
/// </remarks>
internal abstract record LocationArg(string FilePath);

/// <summary>
///     A path-only location (e.g. <c>"Foo.cs"</c>). Used by file-scoped tools that take a
///     path but no cursor.
/// </summary>
internal sealed record FileLocation(string FilePath) : LocationArg(FilePath);

/// <summary>
///     A location that carries at least a 1-based line — the common base for
///     <see cref="LineLocation" /> and <see cref="CursorLocation" />.
/// </summary>
internal abstract record PositionLocation(string FilePath, int Line) : LocationArg(FilePath);

/// <summary>
///     A position with a line but no column (e.g. <c>"Foo.cs:42"</c>).
/// </summary>
internal sealed record LineLocation(string FilePath, int Line) : PositionLocation(FilePath, Line);

/// <summary>
///     A full cursor with both line and column (e.g. <c>"Foo.cs:42:18"</c>).
/// </summary>
internal sealed record CursorLocation(string FilePath, int Line, int Column) : PositionLocation(FilePath, Line);
