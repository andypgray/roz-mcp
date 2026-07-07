using TestFixture.Shapes;

namespace TestFixture.Services;

public class ShapeProcessor<T> where T : Shape
{
    private readonly T _shape;

    public ShapeProcessor(T shape)
    {
        _shape = shape;
    }

    public string Describe() => _shape.Describe();
}
