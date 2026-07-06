namespace TestFixture.Shapes;

/// <summary>
///     Represents a geometric shape with calculable area and perimeter.
/// </summary>
public interface IShape
{
    /// <summary>Gets the area of the shape in square units.</summary>
    double Area { get; }

    /// <summary>Gets the perimeter of the shape in linear units.</summary>
    double Perimeter { get; }

    /// <summary>
    ///     Returns a human-readable description of the shape.
    /// </summary>
    /// <returns>A string describing the shape's type and dimensions.</returns>
    string Describe();
}
