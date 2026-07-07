using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zphil.Roz.Pipeline;

/// <summary>
///     Coerces stringified <c>bool</c> tool inputs (<c>"true"</c>, <c>"FALSE"</c>) into the
///     real boolean the caller clearly intended. Models routinely send these for parameters
///     like <c>includeBody</c>, <c>incremental</c>, or <c>isRegex</c>; the SDK default
///     surfaces them as a generic byte-position deserializer error because
///     <see cref="JsonSerializerDefaults.Web" />'s <c>AllowReadingFromString</c> covers numbers
///     only, not booleans.
/// </summary>
/// <remarks>
///     <para>
///         Handled token shapes for any <c>bool</c> (or <c>bool?</c>) parameter:
///     </para>
///     <list type="bullet">
///         <item>
///             <description><c>True</c> / <c>False</c> → pass through unchanged.</description>
///         </item>
///         <item>
///             <description>
///                 <c>String</c> whose trimmed value is <c>"true"</c> or <c>"false"</c>
///                 (case-insensitive) → coerce to the matching boolean.
///             </description>
///         </item>
///         <item>
///             <description>
///                 Any other token (numeric, null, object, array, non-bool string) → throw
///                 <see cref="UserErrorException" /> naming the offending token kind.
///             </description>
///         </item>
///     </list>
///     <para>
///         Strict by design: <c>0</c>/<c>1</c> numerics are rejected. The principled coercions
///         for <c>string[]</c> all collapsed to a clear string identity; bool has no analogous
///         principle, and accepting <c>1</c> opens follow-on questions (<c>2</c>? <c>-1</c>?
///         empty string?) that are better answered by a clean error.
///     </para>
///     <para>
///         <see cref="System.Text.Json" /> unwraps <see cref="Nullable{T}" /> before invoking
///         the converter, so <see cref="CanConvert" /> on <c>typeof(bool)</c> covers both
///         <c>bool</c> and <c>bool?</c> parameters.
///     </para>
/// </remarks>
internal sealed class BoolCoercerFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(bool);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => new BoolCoercer();

    private sealed class BoolCoercer : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.True:
                    return true;

                case JsonTokenType.False:
                    return false;

                case JsonTokenType.String:
                    string raw = reader.GetString()!;
                    ReadOnlySpan<char> trimmed = raw.AsSpan().Trim();
                    if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    throw new UserErrorException(
                        $"Expected a boolean (true/false); got string \"{raw}\".");

                default:
                    throw new UserErrorException(
                        $"Expected a boolean (true/false); got {reader.TokenType}.");
            }
        }

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
            writer.WriteBooleanValue(value);
    }
}
