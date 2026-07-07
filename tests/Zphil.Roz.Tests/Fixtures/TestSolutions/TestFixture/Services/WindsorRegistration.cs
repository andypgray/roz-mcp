using Castle.MicroKernel.Registration;
using Castle.Windsor;
using TestFixture.Shapes;

namespace TestFixture.Services;

public static class WindsorRegistration
{
    public static void Configure(IWindsorContainer container)
    {
        // Fluent chain with lifestyle method
        container.Register(Component.For<IShape>().ImplementedBy<Circle>().LifestyleSingleton());

        // Transient
        container.Register(Component.For<ShapeService>().LifestyleTransient());
    }
}
