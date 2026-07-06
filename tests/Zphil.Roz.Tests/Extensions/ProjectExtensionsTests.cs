using Zphil.Roz.Extensions;

namespace Zphil.Roz.Tests.Extensions;

public class ProjectExtensionsTests
{
    [Theory]
    [InlineData("Orleans.Core(net8.0)", "Orleans.Core")] // strips (tfm)
    [InlineData("Orleans.Core", "Orleans.Core")] // no paren → passthrough
    [InlineData("(foo)", "(foo)")] // leading paren (parenIndex==0) NOT stripped
    [InlineData("Foo(net8.0", "Foo(net8.0")] // '(' without ')' → passthrough
    public void StripTfmSuffix_VariousNames_ReturnsExpected(string input, string expected) =>
        ProjectExtensions.StripTfmSuffix(input).ShouldBe(expected);
}
