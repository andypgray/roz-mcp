using System.Text.Json;
using Zphil.Roz.Enums;
using Zphil.Roz.Pipeline;

namespace Zphil.Roz.Tests.Pipeline;

/// <summary>
///     Tests for <see cref="EnumArrayCoercerFactory" />: every enum-array tool parameter
///     (e.g. <c>SymbolicKind[]?</c> on <c>find_symbol</c>'s <c>memberKinds</c>) accepts a plain
///     JSON array, a stringified JSON array, or a bare string (single-coerce), validates each
///     element against <see cref="Enum.IsDefined" />, and rejects everything else with a friendly
///     <see cref="UserErrorException" />.
/// </summary>
public class EnumArrayCoercerFactoryTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new EnumArrayCoercerFactory() }
    };

    [Fact]
    public void Deserialize_PlainArray_ReturnsValues()
    {
        // Act — the canonical, well-formed input shape.
        SymbolicKind[] result = JsonSerializer.Deserialize<SymbolicKind[]>("""["Method","Property"]""", Options)!;

        // Assert
        result.ShouldBe([SymbolicKind.Method, SymbolicKind.Property]);
    }

    [Fact]
    public void Deserialize_ScalarString_ReturnsSingleElementArray()
    {
        // Act — bare scalar where the model meant a single-element array.
        SymbolicKind[] result = JsonSerializer.Deserialize<SymbolicKind[]>("\"Method\"", Options)!;

        // Assert
        result.ShouldBe([SymbolicKind.Method]);
    }

    [Fact]
    public void Deserialize_StringifiedJsonArray_Unwraps()
    {
        // Arrange — outer JSON is a string whose contents are themselves a JSON array.
        // The dominant malformed shape the model produces.
        string json = JsonSerializer.Serialize("""["Method","Property"]""");

        // Act
        SymbolicKind[] result = JsonSerializer.Deserialize<SymbolicKind[]>(json, Options)!;

        // Assert
        result.ShouldBe([SymbolicKind.Method, SymbolicKind.Property]);
    }

    [Fact]
    public void Deserialize_StringifiedJsonArrayWithWhitespace_Unwraps()
    {
        // Arrange — tolerate whitespace around the inner JSON.
        string json = JsonSerializer.Serialize("""  ["Method"]  """);

        // Act
        SymbolicKind[] result = JsonSerializer.Deserialize<SymbolicKind[]>(json, Options)!;

        // Assert
        result.ShouldBe([SymbolicKind.Method]);
    }

    [Fact]
    public void Deserialize_CaseInsensitive_Matches()
    {
        // Act — lowercase scalar should parse via ignoreCase.
        SymbolicKind[] result = JsonSerializer.Deserialize<SymbolicKind[]>("\"method\"", Options)!;

        // Assert
        result.ShouldBe([SymbolicKind.Method]);
    }

    [Fact]
    public void Deserialize_CaseInsensitiveInArray_Matches()
    {
        // Act — lowercase inside a plain array.
        SymbolicKind[] result = JsonSerializer.Deserialize<SymbolicKind[]>("""["method","PROPERTY"]""", Options)!;

        // Assert
        result.ShouldBe([SymbolicKind.Method, SymbolicKind.Property]);
    }

    [Fact]
    public void Deserialize_EmptyArray_ReturnsEmpty()
    {
        // Act — empty array passes through verbatim. Downstream filters treat null and length-0
        // the same; we do NOT coerce to null. Matches StringArrayCoercerFactory's semantics.
        SymbolicKind[] result = JsonSerializer.Deserialize<SymbolicKind[]>("[]", Options)!;

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public void Deserialize_Null_ReturnsNull_ForNullableArray()
    {
        // Act — STJ short-circuits null for reference types before invoking the converter.
        SymbolicKind[]? result = JsonSerializer.Deserialize<SymbolicKind[]?>("null", Options);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public void Deserialize_UnknownName_ThrowsWithValidList()
    {
        // Act — unknown enum name surfaces the same valid-values message as
        // EnumValidationConverterFactory; the model can self-correct on the next call.
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<SymbolicKind[]>("""["Frobnicate"]""", Options));

        // Assert — message contains the bad value and every valid enum name.
        ex.Message.ShouldContain("\"Frobnicate\"");
        foreach (string name in Enum.GetNames<SymbolicKind>())
        {
            ex.Message.ShouldContain(name);
        }
    }

    [Fact]
    public void Deserialize_UnknownNameInStringifiedArray_ThrowsWithValidList()
    {
        // Arrange — stringified-array path also propagates friendly enum errors,
        // not the JsonException fallback to single-coerce.
        string json = JsonSerializer.Serialize("""["Frobnicate"]""");

        // Act
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<SymbolicKind[]>(json, Options));

        // Assert
        ex.Message.ShouldContain("\"Frobnicate\"");
        ex.Message.ShouldContain("Method");
    }

    [Fact]
    public void Deserialize_UnknownScalar_ThrowsWithValidList()
    {
        // Act — single-coerce path also validates the enum name.
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<SymbolicKind[]>("\"Frobnicate\"", Options));

        // Assert
        ex.Message.ShouldContain("\"Frobnicate\"");
        ex.Message.ShouldContain("Method");
    }

    [Fact]
    public void Deserialize_NumericElement_ThrowsUserError()
    {
        // Act — explicit policy: integers are NOT admitted as enum values, even though
        // Enum.TryParse on a number-string would succeed. Plain array path.
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<SymbolicKind[]>("[42]", Options));

        // Assert — message names the offending token kind so the model can self-correct.
        ex.Message.ShouldContain("Number");
    }

    [Fact]
    public void Deserialize_NumericElement_FromStringifiedArray_ThrowsUserError()
    {
        // Arrange — stringified array containing a number is not a valid string array;
        // TryParseAsJsonStringArray rejects it, then single-coerce treats the whole string
        // as one enum name, which fails with the valid-values message.
        string json = JsonSerializer.Serialize("[42]");

        // Act
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<SymbolicKind[]>(json, Options));

        // Assert — verbatim string "[42]" surfaces as the bad enum name.
        ex.Message.ShouldContain("[42]");
    }

    [Fact]
    public void Deserialize_Number_ThrowsUserError()
    {
        // Act — top-level number is the wrong token kind entirely.
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<SymbolicKind[]>("42", Options));

        // Assert
        ex.Message.ShouldContain("Number");
    }

    [Fact]
    public void Deserialize_NumericStringElement_InArray_ThrowsUserError()
    {
        // Act — CR-6: a numeric STRING ("5") would bind to ordinal 5 (Method) via Enum.TryParse,
        // violating the "integers not admitted" contract. Reject it with the valid-values message.
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<SymbolicKind[]>("""["5"]""", Options));

        // Assert — bad value plus a valid name so the model uses the name, not the integer.
        ex.Message.ShouldContain("\"5\"");
        ex.Message.ShouldContain("Method");
    }

    [Fact]
    public void Deserialize_NumericStringScalar_ThrowsUserError()
    {
        // Act — CR-6: the single-coerce (bare scalar) path also rejects a numeric string.
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<SymbolicKind[]>("\"5\"", Options));

        // Assert
        ex.Message.ShouldContain("\"5\"");
        ex.Message.ShouldContain("Method");
    }

    [Fact]
    public void Deserialize_NumericStringElement_FromStringifiedArray_ThrowsUserError()
    {
        // Arrange — CR-6: the stringified-array path maps each element through the same guard.
        string json = JsonSerializer.Serialize("""["5"]""");

        // Act
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<SymbolicKind[]>(json, Options));

        // Assert
        ex.Message.ShouldContain("\"5\"");
        ex.Message.ShouldContain("Method");
    }

    [Fact]
    public void Deserialize_Object_ThrowsUserError()
    {
        // Act
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<SymbolicKind[]>("{}", Options));

        // Assert
        ex.Message.ShouldContain("StartObject");
    }

    [Fact]
    public void Deserialize_Boolean_ThrowsUserError()
    {
        // Act
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<SymbolicKind[]>("true", Options));

        // Assert
        ex.Message.ShouldContain("True");
    }

    [Fact]
    public void Deserialize_NullElement_ThrowsUserError()
    {
        // Act — null elements are rejected so downstream consumers don't NRE on them.
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<SymbolicKind[]>("""["Method",null]""", Options));

        // Assert
        ex.Message.ShouldContain("Null");
    }

    [Fact]
    public void Deserialize_EditSymbolAction_RoundTrips()
    {
        // Act — the factory is generic over any enum, not just SymbolicKind.
        EditSymbolAction[] result = JsonSerializer.Deserialize<EditSymbolAction[]>("""["Replace","Remove"]""", Options)!;

        // Assert
        result.ShouldBe([EditSymbolAction.Replace, EditSymbolAction.Remove]);
    }

    [Fact]
    public void Deserialize_UnclosedArray_ThrowsJsonException()
    {
        // Act — STJ's outer parser rejects truncated JSON before it ever reaches the converter,
        // but the EndOfStream guard inside ReadArray exists to keep the converter safe if a
        // future caller hands it a primed Utf8JsonReader. Symmetric with StringArrayCoercerFactory.
        Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<SymbolicKind[]>("""["Method","Property" """, Options));
    }
}
