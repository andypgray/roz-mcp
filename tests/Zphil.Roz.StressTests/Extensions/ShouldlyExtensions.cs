namespace Zphil.Roz.StressTests.Extensions;

/// <summary>
///     Overrides Shouldly's default case-insensitive string assertions with case-sensitive ones.
///     To opt into case-insensitive, pass <see cref="Case.Insensitive" /> explicitly.
/// </summary>
internal static class ShouldlyExtensions
{
    internal static void ShouldContain(this string actual, string expected) =>
        actual.ShouldContain(expected, Case.Sensitive);

    internal static void ShouldNotContain(this string actual, string expected) =>
        actual.ShouldNotContain(expected, Case.Sensitive);
}
