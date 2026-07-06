namespace TestFixture.Shapes;

/// <summary>
///     A hexagon with an intentional compilation error (CS0246: NonExistentType).
///     Tests that tools handle types with errors correctly.
/// </summary>
public class Hexagon(double side) : Shape, IEquatable<NonExistentType>
{
    public double Side { get; } = side;
    public override double Area => 2.598 * Side * Side;
    public override double Perimeter => 6 * Side;
    public bool Equals(NonExistentType? other) => false;
}
