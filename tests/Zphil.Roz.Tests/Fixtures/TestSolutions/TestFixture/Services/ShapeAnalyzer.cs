using TestFixture.Shapes;

namespace TestFixture.Services;

/// <summary>
///     Analyzes shapes with type-specific methods — used to test receiver-type filtering
///     in find_references referenceKinds=invocations when containingType is specified.
/// </summary>
public class ShapeAnalyzer
{
    public string AnalyzeCircle(Circle circle) =>
        circle.Describe();

    public string AnalyzeRectangle(Rectangle rectangle) =>
        rectangle.Describe();

    public string AnalyzeGenericShape(IShape shape) =>
        shape.Describe();

    public double SumAreas(Circle c, Rectangle r) =>
        c.Area + r.Area;
}
