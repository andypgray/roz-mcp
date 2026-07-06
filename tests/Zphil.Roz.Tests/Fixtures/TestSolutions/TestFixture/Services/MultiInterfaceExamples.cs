namespace TestFixture.Services;

/// <summary>
///     Interfaces sharing a method name for multi-interface implementation tests.
/// </summary>
public interface IAlpha
{
    void Handle();
}

public interface IBeta
{
    void Handle();
}

/// <summary>
///     Implicitly implements <c>Handle()</c> for both <see cref="IAlpha" /> and <see cref="IBeta" />.
/// </summary>
public class AlphaBetaHandler : IAlpha, IBeta
{
    public void Handle()
    {
    }
}

/// <summary>
///     Base class providing an implementation that a derived class inherits to satisfy an interface.
/// </summary>
public class HandlerBase
{
    public void Run()
    {
    }
}

public interface IRunner
{
    void Run();
}

/// <summary>
///     Inherits <c>Run()</c> from <see cref="HandlerBase" /> to satisfy <see cref="IRunner.Run" />.
/// </summary>
public class InheritingRunner : HandlerBase, IRunner;

/// <summary>
///     Stand-alone class with a virtual method but no interface — regression guard for the tip.
/// </summary>
public class StandaloneVirtualMethods
{
    public virtual void DoWork()
    {
    }
}
