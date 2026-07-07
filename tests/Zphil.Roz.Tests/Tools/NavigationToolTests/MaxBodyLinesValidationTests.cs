using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.NavigationToolTests;

/// <summary>
///     CR-12: a negative <c>maxBodyLines</c> reaches <c>SymbolFormatter</c>'s body slice
///     (<c>bodyLines[..cap]</c>) and throws an unfriendly <see cref="ArgumentOutOfRangeException" />.
///     The four tools exposing <c>maxBodyLines</c> validate it up front and surface a correctable
///     <see cref="UserErrorException" /> instead. (Zero is valid — an empty body slice.)
/// </summary>
public class MaxBodyLinesValidationTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools navigationTools = TestFileHelper.CreateNavigationTools(fixture);
    private readonly ReferenceTools referenceTools = TestFileHelper.CreateReferenceTools(fixture);

    [Fact]
    public async Task FindSymbol_NegativeMaxBodyLines_ThrowsUserError()
    {
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() =>
            navigationTools.FindSymbol(["Circle"], maxBodyLines: -1, ct: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("maxBodyLines");
    }

    [Fact]
    public async Task FindOverloads_NegativeMaxBodyLines_ThrowsUserError()
    {
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() =>
            navigationTools.FindOverloads(symbolNames: ["Circle"], maxBodyLines: -1, ct: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("maxBodyLines");
    }

    [Fact]
    public async Task GoToDefinition_NegativeMaxBodyLines_ThrowsUserError()
    {
        // The validation is the first statement, ahead of location parsing, so it fires regardless
        // of whether the location resolves.
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() =>
            navigationTools.GoToDefinition("Shapes/Circle.cs:10:10", maxBodyLines: -1, ct: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("maxBodyLines");
    }

    [Fact]
    public async Task FindImplementations_NegativeMaxBodyLines_ThrowsUserError()
    {
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() =>
            referenceTools.FindImplementations(symbolNames: ["IShape"], maxBodyLines: -1, ct: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("maxBodyLines");
    }
}
