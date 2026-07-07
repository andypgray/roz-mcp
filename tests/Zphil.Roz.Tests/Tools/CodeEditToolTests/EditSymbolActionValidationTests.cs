using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Validates per-action required-field guards on the merged <c>edit_symbol</c> tool.
///     The JSON schema cannot express "required when action=X", so the guards live in code.
///     Shape errors are reported per-op alongside runtime errors, not as a whole-batch abort.
/// </summary>
public class EditSymbolActionValidationTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task EditSymbol_Batch_ShapeInvalid_ReportsPerOp()
    {
        // Arrange
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circleFile = CircleFile(ws);

        // Act — op 1 (Remove) is valid; op 2 (Replace, missing NewDeclaration) and op 3
        // (Insert, missing Content) are shape-invalid. All three ops should run; shape
        // errors are captured per-op and do not abort the batch.
        EditSymbolBatchOutcome outcome = await tools.SymbolEditServiceForTests.EditSymbolBatchAsync([
            new EditSymbolRequest(
                EditSymbolAction.Remove,
                circleFile,
                "Radius"),
            new EditSymbolRequest(
                EditSymbolAction.Replace,
                circleFile,
                "Diameter"),
            new EditSymbolRequest(
                EditSymbolAction.Insert,
                circleFile,
                "Diameter")
        ], ct: TestContext.Current.CancellationToken);
        IReadOnlyList<EditSymbolOpResult> results = outcome.Ops;

        // Assert
        results.Count.ShouldBe(3);
        results[0].ShouldBeOfType<EditSymbolRemoveOp>();

        EditSymbolErrorOp replaceErr = results[1].ShouldBeOfType<EditSymbolErrorOp>();
        replaceErr.Action.ShouldBe(EditSymbolAction.Replace);
        replaceErr.Error.ShouldContain("newDeclaration is required");

        EditSymbolErrorOp insertErr = results[2].ShouldBeOfType<EditSymbolErrorOp>();
        insertErr.Action.ShouldBe(EditSymbolAction.Insert);
        insertErr.Error.ShouldContain("content is required");

        (await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken)).ShouldNotContain("public double Radius");
    }
}
