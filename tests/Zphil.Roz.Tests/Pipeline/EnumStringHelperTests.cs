using Zphil.Roz.Pipeline;

namespace Zphil.Roz.Tests.Pipeline;

/// <summary>
///     Unit tests for <see cref="EnumStringHelper.LooksNumeric" /> — the CR-6 guard that keeps
///     numeric strings from binding to enum ordinals. A leading-digit check is insufficient, so
///     these pin the trap cases the review's first fix would have missed.
/// </summary>
public class EnumStringHelperTests
{
    [Theory]
    [InlineData("5")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("+5")]
    [InlineData(" 5 ")]
    [InlineData("5 ")]
    [InlineData(" 5")]
    [InlineData("99999999999999999999999")] // wider than Int64 — caught by BigInteger, not long
    public void LooksNumeric_IntegerStrings_ReturnsTrue(string value) =>
        EnumStringHelper.LooksNumeric(value).ShouldBeTrue();

    [Theory]
    [InlineData("Method")]
    [InlineData("Method5")]
    [InlineData("5Method")]
    [InlineData("5x")]
    [InlineData("0x10")]
    [InlineData("")]
    [InlineData("   ")]
    public void LooksNumeric_NonIntegerStrings_ReturnsFalse(string value) =>
        EnumStringHelper.LooksNumeric(value).ShouldBeFalse();
}
