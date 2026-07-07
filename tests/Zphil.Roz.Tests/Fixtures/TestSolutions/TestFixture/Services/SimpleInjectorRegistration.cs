using SimpleInjector;
using TestFixture.Shapes;

namespace TestFixture.Services;

public static class SimpleInjectorRegistration
{
    public static void Configure(Container container)
    {
        // Explicit lifestyle parameter
        container.Register<IShape, Circle>(Lifestyle.Singleton);

        // Transient (explicit)
        container.Register<ShapeService>(Lifestyle.Transient);

        // Scoped
        container.Register<ShapeCalculator>(Lifestyle.Scoped);
    }
}
