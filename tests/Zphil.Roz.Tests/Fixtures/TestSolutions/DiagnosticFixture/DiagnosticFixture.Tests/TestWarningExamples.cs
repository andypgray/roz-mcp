using Xunit;

namespace DiagnosticFixture.Tests;

/// <summary>
///     Intentionally produces a CS0219 warning in a test project,
///     so the includeTests parameter can be verified.
/// </summary>
public class TestWarningExamples
{
    [Fact]
    public void TestMethod_WithUnusedVariable()
    {
        // CS0219: variable assigned but its value is never used
        int testUnusedLocal = 99;
    }
}
