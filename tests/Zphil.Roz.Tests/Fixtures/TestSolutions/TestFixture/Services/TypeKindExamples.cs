using TestFixture.Shapes;

namespace TestFixture.Services;

public struct Point(double x, double y)
{
    public double X { get; } = x;
    public double Y { get; } = y;

    public double DistanceTo(Point other) =>
        Math.Sqrt(Math.Pow(X - other.X, 2) + Math.Pow(Y - other.Y, 2));
}

public enum ShapeColor
{
    Red,
    Green,
    Blue,
    Yellow
}

public delegate double ShapeMetricFunc(IShape shape);

public partial class PartialShapeProcessor
{
    public string ProcessName(string name) => $"Processed: {name}";
}

public partial class PartialShapeProcessor
{
    public int ProcessCount { get; set; }
}

public record ShapeSnapshot(string Name, double Area);

public record struct ShapeId(int Value);

public readonly record struct ReadonlyShapeId(int Value);

[Flags]
public enum ShapeFeatures
{
    None = 0,
    Resizable = 1,
    Rotatable = 2,
    Colorable = 4
}

public sealed class FinalShape(string name)
{
    public string Name { get; } = name;
}

public static class ShapeLabelExtensions
{
    public static string Label(this IShape shape, string prefix = "Shape") =>
        $"{prefix}: {shape.Describe()}";

    public static T WithDescription<T>(this T shape, string desc) where T : IShape =>
        shape;
}

public class OuterContainer
{
    public class InnerProcessor
    {
        public string Process() => "inner";
        public int Count { get; set; }
    }

    public InnerProcessor CreateProcessor() => new();
}
