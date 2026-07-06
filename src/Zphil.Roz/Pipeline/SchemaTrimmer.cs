using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Zphil.Roz.Pipeline;

/// <summary>
///     Alternative to
///     <see
///         cref="McpServerBuilderExtensions.WithTools(IMcpServerBuilder, IEnumerable{Type}, System.Text.Json.JsonSerializerOptions?)" />
///     that shrinks the JSON each tool contributes to a <c>tools/list</c> response. Three trims are
///     applied; together they cut roughly 15% off the wire payload without changing tool behaviour:
///     <list type="bullet">
///         <item>
///             <description>
///                 Inline <c>enum</c> arrays are removed from parameter schemas. The set of legal
///                 values is carried in the parameter description text, which the SDK already emits.
///             </description>
///         </item>
///         <item>
///             <description>
///                 Nullable type unions (<c>"type":["string","null"]</c>) collapse to the non-null
///                 alternative. Optionality is already signalled by the parameter's absence from
///                 <c>required</c>.
///             </description>
///         </item>
///         <item>
///             <description>
///                 Redundant <c>"default":null</c> entries are dropped. Meaningful defaults
///                 (<c>"default":10</c>, <c>"default":"Contains"</c>) are preserved.
///             </description>
///         </item>
///     </list>
///     <para>
///         After <see cref="McpServerTool.Create(System.Reflection.MethodInfo, object?, McpServerToolCreateOptions?)" />
///         builds each tool, the top-level <see cref="Tool.Title" /> is cleared because
///         <see cref="Tool.Annotations" />.<see cref="ToolAnnotations.Title" /> carries the same
///         string. The SDK's <c>DefaultIgnoreCondition = WhenWritingNull</c> then omits it from the
///         wire payload.
///     </para>
///     <para>
///         Input-side argument binding (enum validation, <c>string[]</c> coercion) lives in
///         <see cref="ToolInputSerializerOptions" />, which this class plugs into
///         <c>McpServerToolCreateOptions.SerializerOptions</c> when creating each tool.
///     </para>
/// </summary>
internal static class SchemaTrimmer
{
    private static readonly AIJsonSchemaCreateOptions SchemaOptions = new()
    {
        TransformSchemaNode = TrimSchemaNode
    };

    /// <summary>
    ///     Registers each <see cref="McpServerToolAttribute" />-annotated method in
    ///     <paramref name="toolMethods" /> as an <see cref="McpServerTool" />, with its input schema
    ///     trimmed and its <see cref="Tool" /> metadata pruned of redundant fields.
    /// </summary>
    public static IMcpServerBuilder WithTrimmedTools(
        this IMcpServerBuilder builder,
        IEnumerable<MethodInfo> toolMethods)
    {
        foreach (MethodInfo toolMethod in toolMethods)
        {
            if (toolMethod.GetCustomAttribute<McpServerToolAttribute>() is null)
            {
                continue;
            }

            Type toolType = toolMethod.DeclaringType
                            ?? throw new InvalidOperationException(
                                $"Tool method '{toolMethod.Name}' has no declaring type.");

            builder.Services.AddSingleton<McpServerTool>(services =>
            {
                McpServerToolCreateOptions options = new()
                {
                    Services = services,
                    SchemaCreateOptions = SchemaOptions,
                    SerializerOptions = ToolInputSerializerOptions.Instance
                };

                McpServerTool tool = toolMethod.IsStatic
                    ? McpServerTool.Create(toolMethod, target: null, options)
                    : McpServerTool.Create(toolMethod, r => CreateTarget(r.Services, toolType), options);

                TrimRedundantMetadata(tool.ProtocolTool);
                return tool;
            });
        }

        return builder;
    }

    private static object CreateTarget(IServiceProvider? services, Type type)
    {
        return services is null
            ? Activator.CreateInstance(type)!
            : ActivatorUtilities.CreateInstance(services, type);
    }

    /// <summary>
    ///     Drops <c>Tool.Title</c> and <c>Annotations.Title</c>: Claude Code dispatches on
    ///     <c>Tool.Name</c>, so the human-readable label is dead weight in every
    ///     <c>tools/list</c> response. <see cref="ToolAnnotations" /> itself stays because
    ///     clients read <c>readOnlyHint</c>/<c>openWorldHint</c> off of it. <c>Tool.Execution</c>
    ///     is also cleared — the SDK populates it with <c>{"taskSupport":"optional"}</c> for
    ///     every async method, but MCP Tasks are experimental and current Claude Code clients do
    ///     not consume the hint.
    /// </summary>
    private static void TrimRedundantMetadata(Tool tool)
    {
        tool.Title = null;
        if (tool.Annotations is not null)
        {
            tool.Annotations.Title = null;
        }
#pragma warning disable MCPEXP001 // ToolExecution is experimental; null assignment is safe.
        tool.Execution = null;
#pragma warning restore MCPEXP001
    }

    /// <summary>
    ///     Walks the generated schema node for a single parameter and applies all trims.
    ///     Works on the <see cref="JsonObject" /> directly so the caller sees the mutated node.
    /// </summary>
    /// <remarks>
    ///     When a custom <see cref="System.Text.Json.Serialization.JsonConverter" /> is registered
    ///     for a type, the exporter cannot infer a JSON shape and emits <c>{}</c>. Re-inject the
    ///     shape here from the underlying CLR type so schema consumers still see the correct wire
    ///     contract:
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 Scalar enums (<see cref="EnumValidationConverterFactory" />) — inject
    ///                 <c>"type": "string"</c>.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 Arrays of enums (<see cref="EnumArrayCoercerFactory" />) — inject
    ///                 <c>"type": "array"</c> and <c>"items": { "type": "string" }</c>.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <c>string[]</c> (<see cref="StringArrayCoercerFactory" />) — inject
    ///                 <c>"type": "array"</c> and <c>"items": { "type": "string" }</c>.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <c>bool</c> (<see cref="BoolCoercerFactory" />) — inject
    ///                 <c>"type": "boolean"</c>.
    ///             </description>
    ///         </item>
    ///     </list>
    /// </remarks>
    private static JsonNode TrimSchemaNode(AIJsonSchemaCreateContext context, JsonNode node)
    {
        if (node is JsonObject obj)
        {
            Type underlying = Nullable.GetUnderlyingType(context.TypeInfo.Type) ?? context.TypeInfo.Type;
            if (underlying.IsEnum && !obj.ContainsKey("type"))
            {
                obj["type"] = "string";
            }
            else if (underlying == typeof(string[]))
            {
                if (!obj.ContainsKey("type"))
                {
                    obj["type"] = "array";
                }

                if (!obj.ContainsKey("items"))
                {
                    obj["items"] = new JsonObject { ["type"] = "string" };
                }
            }
            else if (underlying == typeof(bool) && !obj.ContainsKey("type"))
            {
                obj["type"] = "boolean";
            }
            else if (underlying.IsArray && underlying.GetElementType() is { IsEnum: true })
            {
                if (!obj.ContainsKey("type"))
                {
                    obj["type"] = "array";
                }

                if (!obj.ContainsKey("items"))
                {
                    obj["items"] = new JsonObject { ["type"] = "string" };
                }
            }
        }

        TrimRecursive(node);
        return node;
    }

    private static void TrimRecursive(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("enum");
                CollapseNullableTypeUnion(obj);
                RemoveRedundantNullDefault(obj);

                foreach (KeyValuePair<string, JsonNode?> pair in obj)
                {
                    TrimRecursive(pair.Value);
                }

                break;

            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    TrimRecursive(item);
                }

                break;
        }
    }

    /// <summary>
    ///     Collapses <c>"type":["string","null"]</c> into <c>"type":"string"</c>. Optionality is
    ///     already expressed by the property's absence from the parent schema's <c>required</c>
    ///     array, so the explicit null union is noise. If the union contains more than two entries
    ///     (or no <c>null</c>) it is left alone.
    /// </summary>
    private static void CollapseNullableTypeUnion(JsonObject obj)
    {
        if (obj["type"] is not JsonArray typeArray || typeArray.Count == 0)
        {
            return;
        }

        string? nonNullType = null;
        var sawNull = false;

        foreach (JsonNode? entry in typeArray)
        {
            if (entry is not JsonValue value || !value.TryGetValue(out string? text))
            {
                return;
            }

            if (text == "null")
            {
                sawNull = true;
                continue;
            }

            if (nonNullType is not null)
            {
                // Multiple non-null alternatives — leave the union intact.
                return;
            }

            nonNullType = text;
        }

        if (!sawNull || nonNullType is null)
        {
            return;
        }

        obj["type"] = nonNullType;
    }

    /// <summary>
    ///     Drops <c>"default":null</c>. JSON Schema treats absent properties as optional by default;
    ///     a null default adds no information. Non-null defaults (<c>10</c>, <c>"Contains"</c>)
    ///     are retained because they tell the client what value the server will apply if the
    ///     parameter is omitted.
    /// </summary>
    private static void RemoveRedundantNullDefault(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("default", out JsonNode? value))
        {
            return;
        }

        if (value is null || value.GetValueKind() == JsonValueKind.Null)
        {
            obj.Remove("default");
        }
    }
}
