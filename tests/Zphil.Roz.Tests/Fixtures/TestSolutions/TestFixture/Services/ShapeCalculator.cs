using TestFixture.Shapes;

namespace TestFixture.Services;

public class ShapeCalculator
{
    private static readonly double DefaultRadius;
    private readonly IShape _shape;

    static ShapeCalculator()
    {
        DefaultRadius = 1.0;
    }

    public ShapeCalculator(IShape shape)
    {
        _shape = shape;
    }

    public ShapeCalculator(double radius)
    {
        _shape = new Circle(radius);
    }

    public string Calculate() => _shape.Describe();

    public static double GetDefaultRadius() => DefaultRadius;
}
