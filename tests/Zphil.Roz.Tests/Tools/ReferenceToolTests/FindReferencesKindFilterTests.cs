using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.ReferenceToolTests;

/// <summary>
///     Covers the <c>referenceKinds=reads</c> and <c>referenceKinds=writes</c> filters on <c>find_references</c>.
///     Exercises all the classifier branches: plain assignment, compound assignment,
///     <c>++</c>/<c>--</c>, <c>out</c>/<c>ref</c> arguments, deconstruction, and object initializers.
/// </summary>
public class FindReferencesKindFilterTests(WorkspaceFixture fixture)
{
    private readonly ReferenceTools tools = TestFileHelper.CreateReferenceTools(fixture);

    [Fact]
    public async Task FindReferences_KindsAll_Field_ReturnsReadsAndWrites()
    {
        // Act
        string result = await tools.FindReferences(symbolNames: ["Count"], containingType: "MutableState", referenceKinds: ReferenceKind.All, maxResults: 50, ct: TestContext.Current.CancellationToken);

        // Assert — default header, and both reads and writes present
        result.ShouldContain("References to 'Count'");
        result.ShouldContain("state.Count++");
        result.ShouldContain("int local = state.Count;");
    }

    [Fact]
    public async Task FindReferences_KindsReads_Field_ExcludesWrites()
    {
        // Act
        string result = await tools.FindReferences(symbolNames: ["Count"], containingType: "MutableState", referenceKinds: ReferenceKind.Reads, maxResults: 50, ct: TestContext.Current.CancellationToken);

        // Assert — reads of `state.Count` appear; assignments/increments do not
        result.ShouldContain("Reads of 'Count'");
        result.ShouldContain("int local = state.Count;");
        result.ShouldNotContain("state.Count = value;");
        result.ShouldNotContain("state.Count++;");
        result.ShouldNotContain("state.Count += delta;");
    }

    [Fact]
    public async Task FindReferences_KindsWrites_Field_ReturnsOnlyWrites()
    {
        // Act
        string result = await tools.FindReferences(symbolNames: ["Count"], containingType: "MutableState", referenceKinds: ReferenceKind.Writes, maxResults: 50, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Writes to 'Count'");
        result.ShouldContain("state.Count = value;");
        result.ShouldNotContain("int local = state.Count;");
    }

    [Fact]
    public async Task FindReferences_KindsWrites_CompoundAssignment_Matches()
    {
        // Act
        string result = await tools.FindReferences(symbolNames: ["Count"], containingType: "MutableState", referenceKinds: ReferenceKind.Writes, maxResults: 50, ct: TestContext.Current.CancellationToken);

        // Assert — `state.Count += delta;` is classified as write
        result.ShouldContain("state.Count += delta;");
    }

    [Fact]
    public async Task FindReferences_KindsWrites_PostfixIncrement_Matches()
    {
        // Act
        string result = await tools.FindReferences(symbolNames: ["Count"], containingType: "MutableState", referenceKinds: ReferenceKind.Writes, maxResults: 50, ct: TestContext.Current.CancellationToken);

        // Assert — `state.Count++;` is classified as write
        result.ShouldContain("state.Count++;");
    }

    [Fact]
    public async Task FindReferences_KindsWrites_ObjectInitializer_Matches()
    {
        // Act — MakeWithInitializer has `new() { Count = 7, Name = "init" }`
        string result = await tools.FindReferences(symbolNames: ["Count"], containingType: "MutableState", referenceKinds: ReferenceKind.Writes, maxResults: 50, ct: TestContext.Current.CancellationToken);

        // Assert — initializer assignment of Count counts as a write
        result.ShouldContain("Count = 7");
    }

    [Fact]
    public async Task FindReferences_KindsReads_Property_ExcludesSetters()
    {
        // Act — Name has `get; set;` — ReadName reads, SetName writes
        string result = await tools.FindReferences(symbolNames: ["Name"], containingType: "MutableState", referenceKinds: ReferenceKind.Reads, maxResults: 50, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Reads of 'Name'");
        result.ShouldContain("=> state.Name;");
        result.ShouldNotContain("state.Name = newName;");
    }

    [Fact]
    public async Task FindReferences_KindsWrites_Property_IncludesSetters()
    {
        // Act
        string result = await tools.FindReferences(symbolNames: ["Name"], containingType: "MutableState", referenceKinds: ReferenceKind.Writes, maxResults: 50, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Writes to 'Name'");
        result.ShouldContain("state.Name = newName;");
        result.ShouldNotContain("=> state.Name;");
    }

    [Fact]
    public async Task FindReferences_ExcludeBaseCalls_WithKindsReads_ThrowsUserError()
    {
        // Act — CR-17: excludeBaseCalls is an invocation-only refinement; combining it with an
        // explicit referenceKinds=Reads contradicts the request, so it now throws rather than silently
        // overriding the referenceKinds filter (which the old PromotesKindToInvocations behaviour did).
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() =>
            tools.FindReferences(symbolNames: ["Count"], containingType: "MutableState", referenceKinds: ReferenceKind.Reads, excludeBaseCalls: true, ct: TestContext.Current.CancellationToken));

        // Assert — the message names the conflict and how to resolve it.
        ex.Message.ShouldContain("invocations");
        ex.Message.ShouldContain("reads");
    }

    [Fact]
    public async Task FindReferences_IncludeOverloads_WithKindsAll_PromotesKindToInvocations()
    {
        // Act — includeOverloads forces referenceKinds=invocations, even though referenceKinds=All (default) is passed
        string result = await tools.FindReferences(symbolNames: ["Count"], containingType: "MutableState", referenceKinds: ReferenceKind.All, includeOverloads: true, ct: TestContext.Current.CancellationToken);

        // Assert — "Callers of" is the invocations header; "References to" would be the referenceKinds=All header
        result.ShouldContain("Callers of 'Count'");
        result.ShouldNotContain("References to 'Count'");
    }

    [Fact]
    public async Task FindReferences_KindsWrites_NestedDeconstruction_Matches()
    {
        // Act — NestedDeconstructWrite has `((state.NestedCount, s), trailing) = ((42, "nested"), 0);`
        // `state.NestedCount` is two levels deep in the tuple — still a write.
        string result = await tools.FindReferences(symbolNames: ["NestedCount"], containingType: "MutableState", referenceKinds: ReferenceKind.Writes, maxResults: 50, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Writes to 'NestedCount'");
        result.ShouldContain("NestedCount");
    }

    [Fact]
    public async Task FindReferences_KindsReads_NameofOnMethod_IncludedAsRead()
    {
        // Act — NameOfReadName returns `nameof(ReadName)` — a compile-time symbol reference, not a call.
        string result = await tools.FindReferences(symbolNames: ["ReadName"], containingType: "MutableStateConsumer", referenceKinds: ReferenceKind.Reads, maxResults: 50, ct: TestContext.Current.CancellationToken);

        // Assert — the nameof(ReadName) reference is classified as a read, not an invocation.
        result.ShouldContain("Reads of 'ReadName'");
        result.ShouldContain("nameof(ReadName)");
    }

    [Fact]
    public async Task FindReferences_KindsInvocations_NameofOnMethod_NotClassifiedAsInvocation()
    {
        // Act — search invocations of ReadName; the nameof(ReadName) reference must NOT be returned.
        string result = await tools.FindReferences(symbolNames: ["ReadName"], containingType: "MutableStateConsumer", referenceKinds: ReferenceKind.Invocations, maxResults: 50, ct: TestContext.Current.CancellationToken);

        // Assert — nameof(...) is a compile-time symbol reference, never an invocation.
        result.ShouldNotContain("nameof(ReadName)");
    }
}
