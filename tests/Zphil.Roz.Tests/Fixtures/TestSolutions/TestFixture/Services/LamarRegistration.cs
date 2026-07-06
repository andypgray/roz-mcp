using Lamar;
using TestFixture.Shapes;

namespace TestFixture.Services;

public class LamarRegistration : ServiceRegistry
{
    public LamarRegistration()
    {
        // Singleton via fluent chain
        For<IShape>().Use<Circle>().Singleton();

        // Scoped via fluent chain
        For<ShapeService>().Use<ShapeService>().Scoped();

        // Default (transient) — no explicit lifetime
        For<ShapeCalculator>().Use<ShapeCalculator>();
    }
}
