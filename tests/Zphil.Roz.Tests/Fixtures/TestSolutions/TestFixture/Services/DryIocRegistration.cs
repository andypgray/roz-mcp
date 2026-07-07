using DryIoc;
using TestFixture.Shapes;

namespace TestFixture.Services;

public static class DryIocRegistration
{
    public static void Configure(IContainer container)
    {
        // Explicit reuse parameter
        container.Register<IShape, Circle>(Reuse.Singleton);

        // Transient (default — no reuse parameter)
        container.Register<ShapeService>();

        // Scoped
        container.Register<ShapeCalculator>(Reuse.Scoped);
    }
}
