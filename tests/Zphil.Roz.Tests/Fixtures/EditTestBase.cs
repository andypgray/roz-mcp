namespace Zphil.Roz.Tests.Fixtures;

/// <summary>
///     Base class for edit tests that use <see cref="EditWorkspaceFixture" />.
///     Handles <see cref="IAsyncLifetime" /> by resetting the workspace before each test.
/// </summary>
public abstract class EditTestBase(EditWorkspaceFixture fixture)
    : IClassFixture<EditWorkspaceFixture>, IAsyncLifetime
{
    protected EditWorkspaceFixture Fixture { get; } = fixture;

    public ValueTask InitializeAsync() => new(Fixture.ResetAsync());
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
