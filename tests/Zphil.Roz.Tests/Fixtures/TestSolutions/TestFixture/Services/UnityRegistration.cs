using TestFixture.Shapes;
using Unity;
using Unity.Lifetime;

namespace TestFixture.Services;

public static class UnityRegistration
{
    public static void Configure(IUnityContainer container)
    {
        // Lifetime manager as constructor argument
        container.RegisterType<IShape, Circle>(new ContainerControlledLifetimeManager());

        // Transient (default)
        container.RegisterType<ShapeService>();

        // Hierarchical (scoped)
        container.RegisterType<ShapeCalculator>(new HierarchicalLifetimeManager());
    }
}
