using TestFixture.Services;

namespace TestFixture.Tests;

/// <summary>
///     Cross-assembly consumer of ImpactSurface.Shared(), so AccessibilityNarrow tests can
///     distinguish a same-assembly reference (survives public→internal) from a second-assembly
///     reference (breaks).
/// </summary>
public class ImpactCrossAssemblyConsumer
{
    public int Use() => new ImpactSurface().Shared();
}
