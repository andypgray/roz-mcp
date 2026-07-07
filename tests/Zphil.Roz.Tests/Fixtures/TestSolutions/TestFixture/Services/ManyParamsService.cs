using TestFixture.Shapes;

namespace TestFixture.Services;

/// <summary>
///     Test fixture for compact member listing feature.
///     Contains methods with varying parameter counts to test compact vs full signatures.
/// </summary>
public class ManyParamsService
{
    public ManyParamsService(IShape shape, ShapeCalculator calculator, ShapeService service, string label)
    {
    }

    public string Simple() => "simple";

    public string OneParam(string name) => name;

    public string TwoParams(string name, int count) => $"{name}: {count}";

    public string ThreeParams(string name, int count, bool flag) => $"{name}: {count} ({flag})";

    public string FourParams(string name, int count, bool flag, double value) =>
        $"{name}: {count} ({flag}) = {value}";

    public string FiveParams(string name, int count, bool flag, double value, IShape s) =>
        $"{name}: {count} ({flag}) = {value} [{s.Describe()}]";

    public IShape GetShape() => throw new NotImplementedException();

    public double Area { get; set; }
}
