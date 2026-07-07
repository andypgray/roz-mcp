using TestFixture.Shapes;

namespace TestFixture.Services;

public static class ShapeHelper
{
    public static string FormatAll(IEnumerable<IShape> shapes) =>
        string.Join(", ", shapes.Select(s => s.Describe()));
}
