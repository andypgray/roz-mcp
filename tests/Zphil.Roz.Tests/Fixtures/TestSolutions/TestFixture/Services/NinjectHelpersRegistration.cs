// User code whose root namespace merely shares a leading substring with "Ninject".
// Regression fixture for CR-11b: the Ninject recognizer must claim only the Ninject namespace
// (or Ninject.*) on dotted boundaries, not NinjectHelpers, so RegisterShape here must not be
// reported as a Ninject registration of NinjectHelperTarget.
namespace NinjectHelpers
{
    public static class ServiceConfigurator
    {
        public static void RegisterShape<T>() where T : class
        {
        }
    }
}

namespace TestFixture.Services
{
    public static class NinjectHelpersRegistration
    {
        public static void Configure()
        {
            NinjectHelpers.ServiceConfigurator.RegisterShape<NinjectHelperTarget>();
        }
    }

    public class NinjectHelperTarget
    {
    }
}
