using TestFixture.Shapes;

namespace TestFixture.Services;

/// <summary>
///     Provides operations for processing and formatting shapes.
/// </summary>
public class ShapeService
{
    /// <summary>
    ///     Processes a shape and returns a formatted description.
    /// </summary>
    /// <param name="shape">The shape to process.</param>
    /// <returns>A formatted string with the shape's description.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="shape"/> is null.</exception>
    public string ProcessShape(IShape shape) =>
        $"Processing: {shape.Describe()}";

    /// <summary>
    ///     Returns the shape with the largest area.
    /// </summary>
    /// <param name="shapes">The collection of shapes to compare.</param>
    /// <returns>The <see cref="IShape"/> with the largest area.</returns>
    public IShape GetLargest(IEnumerable<IShape> shapes) =>
        shapes.MaxBy(s => s.Area)!;

    public string Format(IShape shape) =>
        $"{shape.Describe()} (Area: {shape.Area:F2})";

    public string Format(IShape shape, bool includePerimeter) =>
        includePerimeter
            ? $"{shape.Describe()} (Area: {shape.Area:F2}, Perimeter: {shape.Perimeter:F2})"
            : Format(shape);

    public string Format(IShape shape, bool includePerimeter, string prefix) =>
        $"{prefix}: {Format(shape, includePerimeter)}";

    public string DescribeFirst(ShapeCollection collection) =>
        collection[0].Describe();

    public string DescribeWithLabel(IShape shape) => shape.Label("S");

    public Circle TagCircle(Circle c) => c.WithDescription("tagged");

    #region Utilities
    #endregion
}
