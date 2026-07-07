using System.Text.Json;
using Zphil.Roz.Pipeline;

namespace Zphil.Roz.Tests.Pipeline;

/// <summary>
///     Tests for <see cref="BatchRequestArgumentNormalizer" />: <c>edit_symbol</c> and
///     <c>replace_content</c> accept either the canonical <c>edits[]</c> array or a single
///     bare object that the normalizer wraps in place. Non-batch tools are pure pass-through
///     so future tool additions don't accidentally inherit the rescue.
/// </summary>
public class BatchRequestArgumentNormalizerTests
{
    [Fact]
    public void Normalize_ArrayValue_PassesThrough()
    {
        // Arrange — canonical shape stays unchanged.
        Dictionary<string, JsonElement> args = ParseArgs("""
                                                         { "edits": [{ "action": "Replace", "location": "Foo.cs", "symbolName": "Bar" }] }
                                                         """);
        JsonElement original = args["edits"];

        // Act
        BatchRequestArgumentNormalizer.Normalize("edit_symbol", args);

        // Assert — same shape, same length.
        args["edits"].ValueKind.ShouldBe(JsonValueKind.Array);
        args["edits"].GetArrayLength().ShouldBe(1);
        args["edits"].GetRawText().ShouldBe(original.GetRawText());
    }

    [Fact]
    public void Normalize_SingleObject_WrapsAsSingleElementArray_EditSymbol()
    {
        // Arrange — the headline rescue: bare object where an array is advertised.
        Dictionary<string, JsonElement> args = ParseArgs("""
                                                         { "edits": { "action": "Replace", "location": "Foo.cs", "symbolName": "Bar" } }
                                                         """);

        // Act
        BatchRequestArgumentNormalizer.Normalize("edit_symbol", args);

        // Assert — wrapped to a one-element array, original fields preserved verbatim.
        args["edits"].ValueKind.ShouldBe(JsonValueKind.Array);
        args["edits"].GetArrayLength().ShouldBe(1);
        JsonElement inner = args["edits"][0];
        inner.ValueKind.ShouldBe(JsonValueKind.Object);
        inner.GetProperty("action").GetString().ShouldBe("Replace");
        inner.GetProperty("symbolName").GetString().ShouldBe("Bar");
    }

    [Fact]
    public void Normalize_SingleObject_WrapsAsSingleElementArray_ReplaceContent()
    {
        // Arrange — same rescue for the other batch tool.
        Dictionary<string, JsonElement> args = ParseArgs("""
                                                         { "edits": { "filePath": "Foo.cs", "search": "old", "replace": "new" } }
                                                         """);

        // Act
        BatchRequestArgumentNormalizer.Normalize("replace_content", args);

        // Assert
        args["edits"].ValueKind.ShouldBe(JsonValueKind.Array);
        args["edits"].GetArrayLength().ShouldBe(1);
        args["edits"][0].GetProperty("search").GetString().ShouldBe("old");
        args["edits"][0].GetProperty("replace").GetString().ShouldBe("new");
    }

    [Fact]
    public void Normalize_SingleObject_PreservesNestedEscapes()
    {
        // Arrange — verify the GetRawText()-based wrapping doesn't corrupt escapes inside
        // string fields. Both the replace_content patterns and edit_symbol declarations
        // routinely contain backslashes, quotes, and newlines.
        Dictionary<string, JsonElement> args = ParseArgs("""
                                                         { "edits": { "filePath": "Foo.cs", "search": "old\nline", "replace": "new \"quoted\"" } }
                                                         """);

        // Act
        BatchRequestArgumentNormalizer.Normalize("replace_content", args);

        // Assert
        JsonElement inner = args["edits"][0];
        inner.GetProperty("search").GetString().ShouldBe("old\nline");
        inner.GetProperty("replace").GetString().ShouldBe("new \"quoted\"");
    }

    [Fact]
    public void Normalize_EmptyArray_PassesThrough()
    {
        // Arrange — empty array passes through; BatchGuards.RejectEmptyBatch does the final
        // user-facing reject one layer down, so the normalizer does not double up.
        Dictionary<string, JsonElement> args = ParseArgs("""{ "edits": [] }""");

        // Act
        BatchRequestArgumentNormalizer.Normalize("edit_symbol", args);

        // Assert
        args["edits"].ValueKind.ShouldBe(JsonValueKind.Array);
        args["edits"].GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void Normalize_NonBatchTool_PassesThrough()
    {
        // Arrange — find_symbol has no `edits` parameter; even if the model sent one,
        // the normalizer must not touch it. This guards against future scope creep.
        Dictionary<string, JsonElement> args = ParseArgs("""
                                                         { "edits": { "action": "Replace" } }
                                                         """);
        string originalRaw = args["edits"].GetRawText();

        // Act
        BatchRequestArgumentNormalizer.Normalize("find_symbol", args);

        // Assert
        args["edits"].ValueKind.ShouldBe(JsonValueKind.Object);
        args["edits"].GetRawText().ShouldBe(originalRaw);
    }

    [Fact]
    public void Normalize_NullArguments_DoesNotThrow()
    {
        // Act / Assert — defensive, mirrors UnknownParameterGuard's behaviour.
        Should.NotThrow(() => BatchRequestArgumentNormalizer.Normalize("edit_symbol", null));
    }

    [Fact]
    public void Normalize_MissingEdits_PassesThrough()
    {
        // Arrange — missing required argument is caught later by the SDK's binding layer,
        // which raises its own actionable error. The normalizer does not pre-validate.
        Dictionary<string, JsonElement> args = ParseArgs("""{ }""");

        // Act / Assert
        Should.NotThrow(() => BatchRequestArgumentNormalizer.Normalize("edit_symbol", args));
        args.ShouldNotContainKey("edits");
    }

    [Fact]
    public void Normalize_Number_ThrowsUserError()
    {
        // Arrange — wrong token kind at the array slot.
        Dictionary<string, JsonElement> args = ParseArgs("""{ "edits": 42 }""");

        // Act
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            BatchRequestArgumentNormalizer.Normalize("edit_symbol", args));

        // Assert — message names the offending value kind so the model can self-correct.
        ex.Message.ShouldContain("Number");
        ex.Message.ShouldContain("edits");
    }

    [Fact]
    public void Normalize_String_ThrowsUserError()
    {
        // Arrange — string is not a valid shape for a batch param.
        Dictionary<string, JsonElement> args = ParseArgs("""{ "edits": "oops" }""");

        // Act
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            BatchRequestArgumentNormalizer.Normalize("edit_symbol", args));

        // Assert
        ex.Message.ShouldContain("String");
    }

    [Fact]
    public void Normalize_Boolean_ThrowsUserError()
    {
        // Arrange
        Dictionary<string, JsonElement> args = ParseArgs("""{ "edits": true }""");

        // Act
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            BatchRequestArgumentNormalizer.Normalize("edit_symbol", args));

        // Assert
        ex.Message.ShouldContain("True");
    }

    [Fact]
    public void Normalize_Null_ThrowsUserError()
    {
        // Arrange — explicit null where a batch is required.
        Dictionary<string, JsonElement> args = ParseArgs("""{ "edits": null }""");

        // Act
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            BatchRequestArgumentNormalizer.Normalize("edit_symbol", args));

        // Assert
        ex.Message.ShouldContain("Null");
    }

    private static Dictionary<string, JsonElement> ParseArgs(string json)
    {
        // The JsonElement clones are needed because the document gets disposed on exit;
        // without Clone() the elements would point at freed memory by the time the
        // normalizer reads them.
        using var doc = JsonDocument.Parse(json);
        Dictionary<string, JsonElement> args = new(StringComparer.Ordinal);
        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            args[prop.Name] = prop.Value.Clone();
        }

        return args;
    }
}
