using Autofac;
using TestFixture.Shapes;

namespace TestFixture.Services;

public static class AutofacRegistration
{
    public static void Configure(ContainerBuilder builder)
    {
        // Fluent chain with lifetime
        builder.RegisterType<Circle>().As<IShape>().InstancePerLifetimeScope();

        // Singleton
        builder.RegisterType<ShapeService>().AsSelf().SingleInstance();

        // Default (transient) — no explicit lifetime
        builder.RegisterType<ShapeCalculator>().AsSelf();
    }
}
