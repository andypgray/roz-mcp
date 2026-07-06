using TestFixture.Shapes;
using Xunit;

namespace TestFixture.Tests;

public class ShapeTests
{
    [Fact]
    public void Circle_Describe_ReturnsExpected()
    {
        IShape shape = new Circle(5);
        string result = shape.Describe();
        Assert.Contains("Circle", result);
    }
}

/// <summary>
///     Test-only derived class for verifying includeTests on find_implementations.
/// </summary>
internal class TestShape : Shape
{
    public override double Area => 0;
    public override double Perimeter => 0;
}
