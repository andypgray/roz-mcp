using System.Collections.Frozen;
using System.Text.Json;

namespace Zphil.Roz.Pipeline;

/// <summary>
///     Rescues a bare batch-request object sent in place of a one-element array — the object
///     analog of <see cref="StringArrayCoercerFactory" />'s <c>"A"</c> → <c>["A"]</c> rescue.
///     Models routinely send <c>edits: { action: "Replace", … }</c> instead of
///     <c>edits: [{ action: "Replace", … }]</c> when only one edit is needed; the SDK default
///     surfaces this as a generic byte-position deserializer error.
/// </summary>
/// <remarks>
///     <para>
///         Runs in the request-filter pipeline (not as a
///         <see cref="System.Text.Json.Serialization.JsonConverterFactory" />)
///         so the advertised schema retains the full nested record shape. Registering a custom
///         converter for the record-array type would force the schema exporter to emit <c>{}</c>
///         for <c>edits</c>, stripping the field descriptions, defaults, and implicit enum lists
///         that clients infer field shapes from. The other coercer factories
///         (<see cref="StringArrayCoercerFactory" />, <see cref="EnumArrayCoercerFactory" />,
///         <see cref="BoolCoercerFactory" />) compensate via <see cref="SchemaTrimmer" />
///         injection because their shapes are trivially reconstructible; the nested record
///         schema is not, which is what makes the filter approach cleaner here.
///     </para>
///     <para>
///         Scope: <c>edit_symbol</c> and <c>replace_content</c>. New batch tools opt in by
///         adding an entry to <see cref="BatchTools" />.
///     </para>
///     <para>
///         Handled shapes for <c>edits</c>:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <see cref="JsonValueKind.Array" /> → pass through unchanged.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="JsonValueKind.Object" /> → wrap as a single-element array.
///                 The headline rescue.
///             </description>
///         </item>
///         <item>
///             <description>
///                 Any other token (number, string, boolean, null) → throw
///                 <see cref="UserErrorException" /> naming the offending token kind. Empty
///                 arrays pass through; the tool layer's <c>BatchGuards.RejectEmptyBatch</c>
///                 does the final user-facing reject.
///             </description>
///         </item>
///     </list>
/// </remarks>
internal static class BatchRequestArgumentNormalizer
{
    private static readonly FrozenDictionary<string, string> BatchTools =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["edit_symbol"] = "edits",
            ["replace_content"] = "edits"
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    ///     Mutates <paramref name="arguments" /> in place when <paramref name="toolName" /> is
    ///     a batch tool and the batch parameter is sent as a single object instead of an array.
    ///     A pure pass-through for non-batch tools, missing arguments, or already-correct arrays.
    /// </summary>
    public static void Normalize(string toolName, IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
        {
            return;
        }

        if (!BatchTools.TryGetValue(toolName, out string? paramName))
        {
            return;
        }

        if (!arguments.TryGetValue(paramName, out JsonElement value))
        {
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                return;

            case JsonValueKind.Object:
                // Wrap the object as a single-element array. GetRawText() preserves escapes
                // verbatim; surrounding with [] yields a valid JSON array.
                using (var doc = JsonDocument.Parse($"[{value.GetRawText()}]"))
                {
                    arguments[paramName] = doc.RootElement.Clone();
                }

                return;

            default:
                throw new UserErrorException(
                    $"Expected an array of objects for \"{paramName}\"; got {value.ValueKind}.");
        }
    }
}
