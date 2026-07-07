using Ninject;
using TestFixture.Shapes;

namespace TestFixture.Services;

public static class NinjectRegistration
{
    public static void Configure(IKernel kernel)
    {
        // Fluent chain with lifetime
        kernel.Bind<IShape>().To<Circle>().InThreadScope();

        // Singleton
        kernel.Bind<ShapeService>().ToSelf().InSingletonScope();

        // Default (transient) — no explicit lifetime
        kernel.Bind<ShapeCalculator>().ToSelf();
    }
}
