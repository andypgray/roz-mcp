using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zphil.Roz.Pipeline;

/// <summary>
///     Deserializes enum-typed tool parameters from JSON strings. On an unrecognised name
///     (e.g. <c>matchMode: "Glob"</c>) throws a <see cref="UserErrorException" /> that lists
///     every valid value so the model can self-correct the next call.
/// </summary>
/// <remarks>
///     <para>
///         Registered via <see cref="SchemaTrimmer" /> on the <c>McpServerToolCreateOptions.SerializerOptions</c>
///         used by <c>AIFunctionFactory</c> when marshalling JSON-RPC arguments. The default
///         <see cref="JsonStringEnumConverter" /> raises a generic <c>JsonException</c> that
///         surfaces without the valid-value list, forcing the model to guess.
///     </para>
///     <para>
///         Covers every <c>T : struct, Enum</c> in solution-defined parameters
///         (<c>SymbolMatchMode</c>, <c>SymbolicKind</c>, <c>ReferenceKind</c>, <c>EditSymbolAction</c>,
///         <c>InsertPosition</c>, <c>DetailLevel</c>) plus foreign enums that flow through tool
///         parameters, most notably Roslyn's <c>DiagnosticSeverity</c>.
///     </para>
/// </remarks>
internal sealed class EnumValidationConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type converterType = typeof(ValidatingJsonStringEnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }

    private sealed class ValidatingJsonStringEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Our advertised schema is "type": "string"; a client that sends a number is already
            // violating the contract, so reject non-strings with the same valid-values message
            // a bad string would get. This keeps the error surface uniform.
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new UserErrorException(BuildMessage(reader.TokenType.ToString()));
            }

            string? name = reader.GetString();

            // A numeric string ("5", "+5", " 5 ") would bind to an ordinal via Enum.TryParse,
            // violating the "integers not admitted" contract — reject before parsing.
            if (name is not null && EnumStringHelper.LooksNumeric(name))
            {
                throw new UserErrorException(BuildMessage(name));
            }

            if (name is not null && Enum.TryParse(name, true, out T parsed) && Enum.IsDefined(typeof(T), parsed))
            {
                return parsed;
            }

            throw new UserErrorException(BuildMessage(name));
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());

        private static string BuildMessage(string? attempted) =>
            $"Invalid value \"{attempted}\" for parameter. Valid values: {String.Join(", ", Enum.GetNames<T>())}.";
    }
}
