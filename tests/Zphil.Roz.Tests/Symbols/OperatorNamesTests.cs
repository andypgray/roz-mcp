using Zphil.Roz.Symbols;

namespace Zphil.Roz.Tests.Symbols;

public class OperatorNamesTests
{
    [Theory]
    // C# 11 unsigned right shift + checked operators (added to OperatorTokenMap)
    [InlineData("op_UnsignedRightShift")]
    [InlineData("op_CheckedAddition")]
    [InlineData("op_CheckedSubtraction")]
    [InlineData("op_CheckedMultiply")]
    [InlineData("op_CheckedDivision")]
    [InlineData("op_CheckedUnaryNegation")]
    [InlineData("op_CheckedIncrement")]
    [InlineData("op_CheckedDecrement")]
    // Checked conversion is a MethodKind.Conversion — recognized via the special-case, not the token map
    [InlineData("op_CheckedExplicit")]
    public void IsOperatorMetadataName_Csharp11Operators_ReturnsTrue(string metadataName)
    {
        // Act / Assert
        OperatorNames.IsOperatorMetadataName(metadataName).ShouldBeTrue();
    }

    [Theory]
    [InlineData("op_UnsignedRightShift", ">>>")]
    [InlineData("op_CheckedAddition", "checked +")]
    [InlineData("op_CheckedDecrement", "checked --")]
    public void GetDisplayToken_Csharp11Operators_ReturnsSourceToken(string metadataName, string expectedToken)
    {
        // Act
        string token = OperatorNames.GetDisplayToken(metadataName);

        // Assert
        token.ShouldBe(expectedToken);
    }

    [Fact]
    public void IsOperatorMetadataName_PreExistingOperator_StillRecognized()
    {
        // Assert — regression: adding C# 11 entries must not break the original operators
        OperatorNames.IsOperatorMetadataName("op_Addition").ShouldBeTrue();
        OperatorNames.GetDisplayToken("op_Addition").ShouldBe("+");
    }
}
