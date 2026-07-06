namespace TestFixture.Shapes;

public class Circle(double radius) : Shape
{
    public double Radius { get; } = radius;

    public override double Area => Math.PI * Radius * Radius;
    public override double Perimeter => 2 * Math.PI * Radius;
}
