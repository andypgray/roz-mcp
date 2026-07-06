using TestFixture.Shapes;

namespace TestFixture.Services;

public class ShapeCalculatorConsumer
{
    public ShapeCalculator CreateFromShape(IShape shape) => new ShapeCalculator(shape);

    public ShapeCalculator CreateFromRadius(double r) => new ShapeCalculator(r);
}
