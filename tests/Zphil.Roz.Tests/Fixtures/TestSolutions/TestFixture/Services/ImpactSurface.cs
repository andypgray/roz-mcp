namespace TestFixture.Services;

/// <summary>
///     Dedicated surface for analyze_change_impact tests. Each member is consumed in exactly one
///     deterministic way (a wide context, a narrow context, a write, a var-typed adoption, or a
///     same-type call) so per-site verdicts can be asserted without cross-talk.
/// </summary>
public class ImpactSurface
{
    /// <summary>Consumed only into a wider context (double) — a widening TypeChange stays Compatible.</summary>
    public int WidelyConsumed() => 1;

    /// <summary>Consumed only into a narrower context (int) — a widening TypeChange RequiresUpdate.</summary>
    public int NarrowlyConsumed() => 2;

    /// <summary>Referenced from the same type, a sibling type, and a second assembly — for AccessibilityNarrow.</summary>
    public int Shared() => 3;

    /// <summary>Consumed only into a <c>var</c> local — TypeChange is Compatible but ripples.</summary>
    public int Untyped() => 4;

    /// <summary>Same-type caller of Shared(); stays in scope even if Shared() goes private.</summary>
    public int SameTypeUser() => Shared();

    /// <summary>Written into by a sibling type — exercises the consumer (write) value-flow path.</summary>
    public int Setting { get; set; }

    /// <summary>Public, referenced only from a friend assembly — AccessibilityNarrow to internal
    /// stays Compatible via [InternalsVisibleTo].</summary>
    public int FriendVisible() => 5;
}

/// <summary>Same-assembly consumers of <see cref="ImpactSurface" /> exercising each value-flow direction.</summary>
public class ImpactConsumer
{
    private readonly ImpactSurface surface = new();

    public double WideConsumer() => surface.WidelyConsumed();

    public int NarrowConsumer() => surface.NarrowlyConsumed();

    public int CrossTypeUser() => surface.Shared();

    public void Writer() => surface.Setting = 1000;

    public void VarConsumer()
    {
        var value = surface.Untyped();
        System.Console.WriteLine(value);
    }
}
