namespace Zphil.Roz.Symbols;

/// <summary>
///     Composes a location string from its parts — the inverse of the typed
///     <see cref="LocationParser" /> methods. Lives outside the type hierarchy
///     because callers supply nullable <c>int?</c> values without first deciding
///     which <see cref="LocationArg" /> variant to build.
/// </summary>
internal static class LocationFormat
{
    public static string Format(string filePath, int? line = null, int? column = null) =>
        (line, column) switch
        {
            (null, _) => filePath,
            ({ } l, null) => $"{filePath}:{l}",
            ({ } l, { } c) => $"{filePath}:{l}:{c}"
        };
}
