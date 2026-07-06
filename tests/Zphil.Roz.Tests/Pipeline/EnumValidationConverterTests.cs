using System.Text.Json;
using Microsoft.CodeAnalysis;
using Zphil.Roz.Enums;
using Zphil.Roz.Pipeline;

namespace Zphil.Roz.Tests.Pipeline;

/// <summary>
///     Tests for <see cref="EnumValidationConverterFactory" />: every <c>T : struct, Enum</c>
///     routes through a validating converter that throws <c>UserErrorException</c> with the
///     full valid-value list on unknown input.
/// </summary>
public class EnumValidationConverterTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new EnumValidationConverterFactory() }
    };

    [Fact]
    public void Deserialize_ValidName_RoundTrips()
    {
        // Act
        SymbolMatchMode result = JsonSerializer.Deserialize<SymbolMatchMode>("\"StartsWith\"", Options);

        // Assert
        result.ShouldBe(SymbolMatchMode.StartsWith);
    }

    [Fact]
    public void Deserialize_UnknownName_ThrowsUserErrorWithValidList()
    {
        // Act
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<SymbolMatchMode>("\"Glob\"", Options));

        // Assert — message names the bad value and every valid enum name.
        ex.Message.ShouldContain("\"Glob\"");
        ex.Message.ShouldContain("Contains");
        ex.Message.ShouldContain("StartsWith");
        ex.Message.ShouldContain("EndsWith");
        ex.Message.ShouldContain("Exact");
    }

    [Fact]
    public void Deserialize_CaseInsensitiveName_Succeeds()
    {
        // Act — lowercase name should parse via ignoreCase.
        SymbolMatchMode result = JsonSerializer.Deserialize<SymbolMatchMode>("\"exact\"", Options);

        // Assert
        result.ShouldBe(SymbolMatchMode.Exact);
    }

    [Fact]
    public void Deserialize_ForeignEnum_ThrowsWithItsOwnValidNames()
    {
        // Act — Roslyn's DiagnosticSeverity flows through tool parameters but isn't
        // one of our own enums; the factory must handle it too.
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<DiagnosticSeverity>("\"Fatal\"", Options));

        // Assert
        ex.Message.ShouldContain("\"Fatal\"");
        ex.Message.ShouldContain("Error");
        ex.Message.ShouldContain("Warning");
        ex.Message.ShouldContain("Info");
    }

    [Fact]
    public void Deserialize_NumericString_ThrowsUserErrorWithValidList()
    {
        // Act — CR-6: a numeric string ("5") would bind to an ordinal via Enum.TryParse,
        // violating the "integers not admitted" contract. Reject it with the valid-values list.
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<SymbolMatchMode>("\"5\"", Options));

        // Assert — bad value plus valid names so the model self-corrects to a name.
        ex.Message.ShouldContain("\"5\"");
        ex.Message.ShouldContain("Contains");
        ex.Message.ShouldContain("Exact");
    }

    [Fact]
    public void Deserialize_ForeignEnumNumericString_ThrowsUserError()
    {
        // Act — CR-6: foreign enums flow through too; "1" must not bind to DiagnosticSeverity.Info.
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<DiagnosticSeverity>("\"1\"", Options));

        // Assert
        ex.Message.ShouldContain("\"1\"");
        ex.Message.ShouldContain("Warning");
    }
}
