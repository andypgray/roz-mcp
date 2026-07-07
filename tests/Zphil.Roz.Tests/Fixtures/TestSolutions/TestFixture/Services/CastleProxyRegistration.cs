using Castle.DynamicProxy;

namespace TestFixture.Services;

/// <summary>
///     Castle.DynamicProxy usage (proxy creation), NOT a Windsor DI registration. Regression
///     fixture for CR-11a: the Windsor recognizer must claim only the Castle.MicroKernel /
///     Castle.Windsor registration namespaces, never the sibling Castle.DynamicProxy, so this
///     proxy call must not be reported as a Windsor registration of <see cref="CastleProxyTarget" />.
/// </summary>
public static class CastleProxyRegistration
{
    public static void CreateProxy()
    {
        var generator = new ProxyGenerator();
        _ = generator.CreateClassProxy<CastleProxyTarget>();
    }
}

/// <summary>A plain proxyable type used only by <see cref="CastleProxyRegistration" />.</summary>
public class CastleProxyTarget
{
}
