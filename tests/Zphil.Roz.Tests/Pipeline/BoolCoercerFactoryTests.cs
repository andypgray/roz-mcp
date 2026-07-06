using System.Text.Json;
using Zphil.Roz.Pipeline;

namespace Zphil.Roz.Tests.Pipeline;

/// <summary>
///     Tests for <see cref="BoolCoercerFactory" />: every <c>bool</c>/<c>bool?</c> tool parameter
///     accepts a real boolean or a stringified one (<c>"true"</c>/<c>"false"</c>, mixed case),
///     and rejects everything else with a friendly <see cref="UserErrorException" />.
/// </summary>
public class BoolCoercerFactoryTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new BoolCoercerFactory() }
    };

    [Fact]
    public void Deserialize_TrueLiteral_ReturnsTrue()
    {
        // Act — the canonical, well-formed input shape.
        bool result = JsonSerializer.Deserialize<bool>("true", Options);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void Deserialize_FalseLiteral_ReturnsFalse()
    {
        // Act
        bool result = JsonSerializer.Deserialize<bool>("false", Options);

        // Assert
        result.ShouldBeFalse();
    }

    [Theory]
    [InlineData("\"true\"", true)]
    [InlineData("\"false\"", false)]
    [InlineData("\"True\"", true)]
    [InlineData("\"False\"", false)]
    [InlineData("\"TRUE\"", true)]
    [InlineData("\"FALSE\"", false)]
    public void Deserialize_StringifiedBool_CoercesCaseInsensitive(string json, bool expected)
    {
        // Act — the dominant malformed shape the model produces.
        bool result = JsonSerializer.Deserialize<bool>(json, Options);

        // Assert
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("\"  true  \"", true)]
    [InlineData("\"\\tfalse\\n\"", false)]
    public void Deserialize_StringifiedBoolWithSurroundingWhitespace_Coerces(string json, bool expected)
    {
        // Act — tolerate whitespace around the inner value.
        bool result = JsonSerializer.Deserialize<bool>(json, Options);

        // Assert
        result.ShouldBe(expected);
    }

    [Fact]
    public void Deserialize_NullableBool_NullJson_ReturnsNull()
    {
        // Act — STJ short-circuits null for Nullable<T> before invoking the converter,
        // so the factory itself never sees Null for bool?. Locks in that contract.
        bool? result = JsonSerializer.Deserialize<bool?>("null", Options);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public void Deserialize_NullableBool_StringifiedTrue_CoercesToTrue()
    {
        // Act — STJ unwraps Nullable<bool> before calling the converter, so the same
        // coercion path applies to bool? parameters.
        bool? result = JsonSerializer.Deserialize<bool?>("\"true\"", Options);

        // Assert
        result.ShouldBe(true);
    }

    [Fact]
    public void Deserialize_NullableBool_LiteralFalse_ReturnsFalse()
    {
        // Act
        bool? result = JsonSerializer.Deserialize<bool?>("false", Options);

        // Assert
        result.ShouldBe(false);
    }

    [Fact]
    public void Deserialize_Number_ThrowsUserError()
    {
        // Act — strict policy: 0/1 are NOT accepted as bool.
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<bool>("42", Options));

        // Assert — message names the offending token kind so the model can self-correct.
        ex.Message.ShouldContain("Number");
    }

    [Fact]
    public void Deserialize_NumericOne_ThrowsUserError()
    {
        // Act — explicit lock on the "is 1 truthy?" question.
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<bool>("1", Options));

        // Assert
        ex.Message.ShouldContain("Number");
    }

    [Fact]
    public void Deserialize_Null_ThrowsUserError()
    {
        // Act — null for a non-nullable bool is a contract violation.
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<bool>("null", Options));

        // Assert
        ex.Message.ShouldContain("Null");
    }

    [Fact]
    public void Deserialize_Object_ThrowsUserError()
    {
        // Act
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<bool>("{}", Options));

        // Assert
        ex.Message.ShouldContain("StartObject");
    }

    [Fact]
    public void Deserialize_Array_ThrowsUserError()
    {
        // Act
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<bool>("[]", Options));

        // Assert
        ex.Message.ShouldContain("StartArray");
    }

    [Fact]
    public void Deserialize_NonBoolString_ThrowsUserError()
    {
        // Act — strings that aren't true/false should fail clearly.
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<bool>("\"yes\"", Options));

        // Assert — message names the offending value so the model can self-correct.
        ex.Message.ShouldContain("yes");
    }

    [Fact]
    public void Deserialize_EmptyString_ThrowsUserError()
    {
        // Act
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<bool>("\"\"", Options));

        // Assert — the error echoes the offending (empty) value, not just the generic "boolean"
        ex.Message.ShouldContain("got string \"\"");
    }

    [Fact]
    public void Deserialize_StringifiedTrue_WithoutFactory_StillFails()
    {
        // Arrange — regression-lock: Web defaults alone do NOT coerce stringified bools.
        // This documents the gap that justifies BoolCoercerFactory's existence.
        JsonSerializerOptions webOnly = new(JsonSerializerDefaults.Web);

        // Act / Assert
        Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<bool>("\"true\"", webOnly));
    }
}
