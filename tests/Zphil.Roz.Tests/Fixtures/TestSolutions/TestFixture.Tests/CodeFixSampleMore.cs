using Xunit;

namespace TestFixture.Tests;

/// <summary>
///     A second xUnit2004 site in its own file, so an apply_code_fix <c>filePaths</c>/<c>project</c>
///     scope can be shown to fix <see cref="CodeFixSample" /> without touching this one. Restored between
///     edit tests by EditWorkspaceFixture.ResetAsync.
/// </summary>
public class CodeFixSampleMore
{
    [Fact]
    public void BooleanEqual_True_TripsXunit2004()
    {
        bool flag = 3 > 2;
        Assert.Equal(true, flag);
    }
}
