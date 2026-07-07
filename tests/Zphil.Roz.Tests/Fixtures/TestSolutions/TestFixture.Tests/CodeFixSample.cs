using Xunit;

namespace TestFixture.Tests;

/// <summary>
///     apply_code_fix fixture: each <c>[Fact]</c> uses <c>Assert.Equal</c> on a boolean literal, which
///     trips xUnit2004 ("Do not use Assert.Equal() to check for boolean conditions"). Its fixer is
///     FixAll-capable with a single equivalence key, so apply_code_fix resolves it without a flavor
///     choice. Paired with <see cref="CodeFixSampleMore" /> so a file/project-scoped fix can be shown to
///     narrow. Restored between edit tests by EditWorkspaceFixture.ResetAsync.
/// </summary>
public class CodeFixSample
{
    [Fact]
    public void BooleanEqual_True_TripsXunit2004()
    {
        bool flag = 2 > 1;
        Assert.Equal(true, flag);
    }

    [Fact]
    public void BooleanEqual_False_TripsXunit2004()
    {
        bool flag = 2 < 1;
        Assert.Equal(false, flag);
    }
}
