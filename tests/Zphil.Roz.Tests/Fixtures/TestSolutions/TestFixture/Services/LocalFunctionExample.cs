using TestFixture.Shapes;

namespace TestFixture.Services;

public class LocalFunctionExample
{
    public double CalculateTotal(IEnumerable<IShape> shapes)
    {
        double total = 0;
        foreach (var shape in shapes)
        {
            total += GetArea(shape);
        }
        return total;

        double GetArea(IShape s) => s.Area;
    }
}
