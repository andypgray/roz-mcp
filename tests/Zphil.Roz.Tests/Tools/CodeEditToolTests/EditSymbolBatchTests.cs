using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Tests for edit_symbol batch semantics — sectioning, mixed actions, partial failure,
///     sequential same-file ops, per-op scope isolation.
/// </summary>
public class EditSymbolBatchTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    private static string RectangleFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Shapes", "Rectangle.cs");

    private static string MutableStateFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Shapes", "MutableState.cs");

    [Fact]
    public async Task EditSymbol_Batch_MixedActions_AppliesAll()
    {
        // Arrange
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circle = CircleFile(ws);
        string rect = RectangleFile(ws);
        string triangle = TriangleFile(ws);

        // Act — mixed replace/remove/insert across three files, all should succeed.
        string result = await tools.EditSymbol([
            new EditSymbolRequest(
                EditSymbolAction.Replace,
                triangle,
                "Describe",
                """public override string Describe() => "tri";"""),
            new EditSymbolRequest(
                EditSymbolAction.Remove,
                rect,
                "Perimeter"),
            new EditSymbolRequest(
                EditSymbolAction.Insert,
                circle,
                "Area",
                Content: """public string Label => "circle";""",
                Position: InsertPosition.After)
        ], ct: TestContext.Current.CancellationToken);

        // Assert — each op's batch header and sub-result appear.
        result.ShouldContain("=== replace 'Describe' in");
        result.ShouldContain("Triangle.cs");
        result.ShouldContain("=== remove 'Perimeter' in");
        result.ShouldContain("Rectangle.cs");
        result.ShouldContain("=== insert 'Area' in");
        result.ShouldContain("Circle.cs");
        result.ShouldContain("Replaced 'Describe'");
        result.ShouldContain("Removed 'Perimeter'");
        result.ShouldContain("Inserted");

        (await File.ReadAllTextAsync(triangle, TestContext.Current.CancellationToken)).ShouldContain("\"tri\"");
        (await File.ReadAllTextAsync(rect, TestContext.Current.CancellationToken)).ShouldNotContain("Perimeter");
        (await File.ReadAllTextAsync(circle, TestContext.Current.CancellationToken)).ShouldContain("Label");
    }

    [Fact]
    public async Task EditSymbol_Batch_SingleEntry_OmitsHeaderWrapper()
    {
        // Arrange
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string rect = RectangleFile(ws);

        // Act — a single-entry batch should bypass FormatBatch headers (N=1 short-circuit).
        string result = await tools.EditSymbol([
            new EditSymbolRequest(
                EditSymbolAction.Remove,
                rect,
                "Perimeter")
        ], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotContain("=== ");
        result.ShouldContain("Removed 'Perimeter'");
    }

    [Fact]
    public async Task EditSymbol_Batch_MiddleOpFails_OthersSucceed()
    {
        // Arrange
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string triangle = TriangleFile(ws);
        string rect = RectangleFile(ws);

        // Act — middle op targets a symbol that does not exist; outer ops should still apply.
        string result = await tools.EditSymbol([
            new EditSymbolRequest(
                EditSymbolAction.Remove,
                triangle,
                "Describe"),
            new EditSymbolRequest(
                EditSymbolAction.Replace,
                triangle,
                "DoesNotExist",
                "public void DoesNotExist() {}"),
            new EditSymbolRequest(
                EditSymbolAction.Remove,
                rect,
                "Perimeter")
        ], ct: TestContext.Current.CancellationToken);

        // Assert — all three sections present, middle one is an error.
        result.ShouldContain("Removed 'Describe'");
        result.ShouldContain("Error on replace 'DoesNotExist' in");
        result.ShouldContain("Removed 'Perimeter'");

        // Outer ops applied despite middle failure.
        (await File.ReadAllTextAsync(triangle, TestContext.Current.CancellationToken)).ShouldNotContain("Describe");
        (await File.ReadAllTextAsync(rect, TestContext.Current.CancellationToken)).ShouldNotContain("Perimeter");
    }

    [Fact]
    public async Task EditSymbol_Batch_LineOnlyLocation_PerOpError()
    {
        // Arrange — EDIT-1: a `path:line` location (no column) is now normalized to path-only
        // *when a symbolName is supplied* — the line is redundant noise next to the authoritative
        // name, so that op succeeds. The guard is preserved when the name is absent: a bare
        // `path:line` with no symbolName is still genuinely ambiguous and per-op errors.
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string triangle = TriangleFile(ws);
        string rect = RectangleFile(ws);
        string circle = CircleFile(ws);

        // Act
        string result = await tools.EditSymbol([
            new EditSymbolRequest(
                EditSymbolAction.Remove,
                triangle,
                "Describe"),
            new EditSymbolRequest(
                EditSymbolAction.Remove,
                Loc(circle, 5),
                "Radius"),
            new EditSymbolRequest(
                EditSymbolAction.Remove,
                Loc(rect, 6),
                ""),
            new EditSymbolRequest(
                EditSymbolAction.Remove,
                rect,
                "Perimeter")
        ], ct: TestContext.Current.CancellationToken);

        // Assert — `path:line` + symbolName now resolves by name and succeeds; the `path:line`
        // op with NO symbolName still surfaces the per-op `line:col` guard error; the
        // surrounding ops apply.
        result.ShouldContain("Removed 'Describe'");
        result.ShouldContain("Removed 'Radius'");
        result.ShouldContain("Error on remove");
        result.ShouldContain("edit_symbol");
        result.ShouldContain("line:col");
        result.ShouldContain("Removed 'Perimeter'");

        // EDIT-1 op applied (Radius declaration gone); the no-name guard op never touched rect,
        // so the later path-only op still removed Perimeter.
        (await File.ReadAllTextAsync(triangle, TestContext.Current.CancellationToken)).ShouldNotContain("Describe");
        (await File.ReadAllTextAsync(circle, TestContext.Current.CancellationToken)).ShouldNotContain("public double Radius");
        (await File.ReadAllTextAsync(rect, TestContext.Current.CancellationToken)).ShouldNotContain("Perimeter");
    }

    [Fact]
    public async Task EditSymbol_Batch_SameFileTwice_SequentialWorkspaceState()
    {
        // Arrange
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circle = CircleFile(ws);

        // Act — op 2 targets the symbol that op 1 just inserted.
        // Exercises WorkspaceManager.GetSolutionAsync's drain of pending updates.
        string result = await tools.EditSymbol([
            new EditSymbolRequest(
                EditSymbolAction.Insert,
                circle,
                "Area",
                Content: "public int NewlyAdded { get; } = 1;",
                Position: InsertPosition.After),
            new EditSymbolRequest(
                EditSymbolAction.Remove,
                circle,
                "NewlyAdded")
        ], ct: TestContext.Current.CancellationToken);

        // Assert — op 2 found and removed what op 1 inserted.
        string final = await File.ReadAllTextAsync(circle, TestContext.Current.CancellationToken);
        final.ShouldNotContain("NewlyAdded");
        final.ShouldContain("Radius");
        result.ShouldContain("Inserted");
        result.ShouldContain("Removed 'NewlyAdded'");
    }

    [Fact]
    public async Task EditSymbol_Batch_RejectsEmptyArray()
    {
        // Arrange
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() =>
            tools.EditSymbol([]));
        ex.Message.ShouldContain("must not be empty");
    }

    [Fact]
    public async Task EditSymbol_Batch_PerOpContainingTypeDoesNotLeak()
    {
        // Arrange — two ops on same-named symbols in different types. Op 1 scopes via
        // ContainingType, op 2 relies on file-level uniqueness. A leak of op 1's ContainingType
        // into op 2 would fail op 2's resolution.
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circle = CircleFile(ws);
        string rect = RectangleFile(ws);

        // Act
        string result = await tools.EditSymbol([
            new EditSymbolRequest(
                EditSymbolAction.Replace,
                circle,
                "Area",
                "public override double Area => 0;",
                ContainingType: "Circle"),
            new EditSymbolRequest(
                EditSymbolAction.Replace,
                rect,
                "Area",
                "public override double Area => 0;")
        ], ct: TestContext.Current.CancellationToken);

        // Assert — both ops succeed, proving per-op scope isolation.
        result.ShouldContain("Replaced 'Area'");
        result.ShouldContain("Circle.cs");
        result.ShouldContain("Rectangle.cs");
        result.ShouldNotContain("Error");
        (await File.ReadAllTextAsync(circle, TestContext.Current.CancellationToken)).ShouldContain("Area => 0");
        (await File.ReadAllTextAsync(rect, TestContext.Current.CancellationToken)).ShouldContain("Area => 0");
    }

    [Fact]
    public async Task EditSymbol_Batch_PreCancelledToken_Throws_NoChanges()
    {
        // Arrange
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circle = CircleFile(ws);
        string rect = RectangleFile(ws);
        string originalCircle = await File.ReadAllTextAsync(circle, TestContext.Current.CancellationToken);
        string originalRect = await File.ReadAllTextAsync(rect, TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act — cancellation should propagate as OperationCanceledException (not caught per-op).
        await Should.ThrowAsync<OperationCanceledException>(() =>
            tools.EditSymbol(
                [
                    new EditSymbolRequest(
                        EditSymbolAction.Remove,
                        circle,
                        "Perimeter"),
                    new EditSymbolRequest(
                        EditSymbolAction.Remove,
                        rect,
                        "Perimeter")
                ],
                ct: cts.Token));

        // Assert — no op was applied.
        (await File.ReadAllTextAsync(circle, TestContext.Current.CancellationToken)).ShouldBe(originalCircle);
        (await File.ReadAllTextAsync(rect, TestContext.Current.CancellationToken)).ShouldBe(originalRect);
    }

    [Fact]
    public async Task EditSymbol_Batch_SameFileManyRemoves_StaleCursors_AllSucceed()
    {
        // Arrange — EDIT-2 regression: one batch, 5 removes, all in MutableState.cs, each op
        // carrying its *original pre-batch* `:line:col` cursor plus symbolName + containingType.
        // Op 1's removal shifts every later op's lines, so the stale cursors resolve against the
        // (correctly) updated document at the wrong place. Pre-fix this cascades into
        // "past end of line" / "out of range" / "resolved to X but symbolName is Y" failures;
        // with name-authoritative resolution each uniquely-named member resolves regardless.
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string ms = MutableStateFile(ws);

        // Act — cursors are the pre-batch coordinates of each name token in MutableState.cs.
        string result = await tools.EditSymbol([
            new EditSymbolRequest(EditSymbolAction.Remove, Loc(ms, 12, 23), "ReadCount", ContainingType: "MutableStateConsumer"),
            new EditSymbolRequest(EditSymbolAction.Remove, Loc(ms, 18, 24), "IncrementCount", ContainingType: "MutableStateConsumer"),
            new EditSymbolRequest(EditSymbolAction.Remove, Loc(ms, 23, 24), "AssignCount", ContainingType: "MutableStateConsumer"),
            new EditSymbolRequest(EditSymbolAction.Remove, Loc(ms, 28, 24), "CompoundCount", ContainingType: "MutableStateConsumer"),
            new EditSymbolRequest(EditSymbolAction.Remove, Loc(ms, 33, 23), "TryReadCount", ContainingType: "MutableStateConsumer")
        ], ct: TestContext.Current.CancellationToken);

        // Assert — all five removed, none of the stale-cursor failure fingerprints present.
        result.ShouldContain("Removed 'ReadCount'");
        result.ShouldContain("Removed 'IncrementCount'");
        result.ShouldContain("Removed 'AssignCount'");
        result.ShouldContain("Removed 'CompoundCount'");
        result.ShouldContain("Removed 'TryReadCount'");
        result.ShouldNotContain("Error on remove");
        result.ShouldNotContain("past end of line");
        result.ShouldNotContain("out of range");
        result.ShouldNotContain("but symbolName is");

        // Disk: every targeted declaration is gone; the untargeted method survives (positive control).
        string final = await File.ReadAllTextAsync(ms, TestContext.Current.CancellationToken);
        final.ShouldNotContain("int ReadCount(MutableState state)");
        final.ShouldNotContain("void IncrementCount(MutableState state)");
        final.ShouldNotContain("void AssignCount(MutableState state, int value)");
        final.ShouldNotContain("void CompoundCount(MutableState state, int delta)");
        final.ShouldNotContain("int TryReadCount(MutableState state)");
        final.ShouldContain("NestedDeconstructWrite");
    }

    [Fact]
    public async Task EditSymbol_Batch_Overload_StaleCursor_NeverCorruptsWrongOverload()
    {
        // Arrange — ShapeService.cs has 3 `Format` overloads (@27/30/35) and `GetLargest` (@24).
        // Op 1 removes GetLargest, shifting the overloads up; op 2 then targets `Format` with a
        // *stale pre-batch* cursor (35:19, the original 3-arg name token). With >1 name match the
        // cursor only tie-breaks overloads against the identifier-token span — a stale cursor hits
        // none, so the op must either resolve cleanly (fresh cursor) or surface an ambiguity
        // error. It must NEVER silently corrupt the wrong (1-arg) overload.
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string shapeService = ShapeServiceFile(ws);

        // Act
        string result = await tools.EditSymbol([
            new EditSymbolRequest(EditSymbolAction.Remove, Loc(shapeService, 24), "GetLargest"),
            new EditSymbolRequest(EditSymbolAction.Remove, Loc(shapeService, 35, 19), "Format")
        ], ct: TestContext.Current.CancellationToken);

        // Assert — op 1 always succeeds.
        result.ShouldContain("Removed 'GetLargest'");

        string final = await File.ReadAllTextAsync(shapeService, TestContext.Current.CancellationToken);
        final.ShouldNotContain("GetLargest");

        // Invariant (both safe branches): the 1-arg overload is never the removed declaration.
        // `Format(IShape shape) =>` is unique to it (2-/3-arg have `shape,` not `shape)`).
        final.ShouldContain("public string Format(IShape shape) =>");

        // op 2 must take a safe branch — a clean remove or an ambiguity error — never a silent no-op.
        bool formatResolvedOrErrored =
            result.Contains("Removed 'Format'") || result.Contains("Error on remove");
        formatResolvedOrErrored.ShouldBeTrue();
    }

    [Fact]
    public async Task EditSymbol_Batch_Overload_StaleCursorLineOutOfRange_ThrowsAmbiguity()
    {
        // Arrange — the EDIT-2 "Line N out of range. File has M lines" fingerprint, on an
        // overloaded name. Ops 1-2 remove ProcessShape + GetLargest (~17 lines), shrinking
        // ShapeService.cs below 35 lines. Op 3 targets `Format` (3 overloads) with a stale
        // pre-batch cursor at line 35 — now past EOF. The cursor resolves *exactly* (never
        // clamped), so an out-of-range line yields no hit and an actionable ambiguity error,
        // not a crash and not a wrong-overload edit.
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string shapeService = ShapeServiceFile(ws);

        // Act
        string result = await tools.EditSymbol([
            new EditSymbolRequest(EditSymbolAction.Remove, shapeService, "ProcessShape"),
            new EditSymbolRequest(EditSymbolAction.Remove, shapeService, "GetLargest"),
            new EditSymbolRequest(EditSymbolAction.Remove, Loc(shapeService, 35, 19), "Format")
        ], ct: TestContext.Current.CancellationToken);

        // Assert — ops 1-2 succeed; op 3's out-of-range stale cursor → ambiguity, never a crash.
        result.ShouldContain("Removed 'ProcessShape'");
        result.ShouldContain("Removed 'GetLargest'");
        result.ShouldContain("Error on remove");
        result.ShouldContain("ambiguous");
        result.ShouldNotContain("out of range");
        result.ShouldNotContain("past end of line");

        string final = await File.ReadAllTextAsync(shapeService, TestContext.Current.CancellationToken);
        final.ShouldNotContain("ProcessShape");
        final.ShouldNotContain("GetLargest");
        // Invariant: no Format overload was touched — the 1-arg signature is intact.
        final.ShouldContain("public string Format(IShape shape) =>");
    }

    [Fact]
    public async Task EditSymbol_Batch_PathLine_WithSymbolName_ResolvesByName()
    {
        // Arrange — EDIT-1: the middle op uses the `path:line` form (no column) *with* a
        // symbolName. The redundant line is dropped and the op resolves by name instead of
        // throwing the `line:col` guard error; the surrounding ops apply normally.
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string triangle = TriangleFile(ws);
        string circle = CircleFile(ws);
        string rect = RectangleFile(ws);

        // Act
        string result = await tools.EditSymbol([
            new EditSymbolRequest(EditSymbolAction.Remove, triangle, "Describe"),
            new EditSymbolRequest(EditSymbolAction.Remove, Loc(circle, 5), "Radius"),
            new EditSymbolRequest(EditSymbolAction.Remove, rect, "Perimeter")
        ], ct: TestContext.Current.CancellationToken);

        // Assert — all three removed, no per-op error.
        result.ShouldContain("Removed 'Describe'");
        result.ShouldContain("Removed 'Radius'");
        result.ShouldContain("Removed 'Perimeter'");
        result.ShouldNotContain("Error");

        (await File.ReadAllTextAsync(triangle, TestContext.Current.CancellationToken)).ShouldNotContain("Describe");
        (await File.ReadAllTextAsync(circle, TestContext.Current.CancellationToken)).ShouldNotContain("public double Radius");
        (await File.ReadAllTextAsync(rect, TestContext.Current.CancellationToken)).ShouldNotContain("Perimeter");
    }
}
