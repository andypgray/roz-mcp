namespace TestFixture.Services;

/// <summary>Interface member for the slot-family / interface-dispatch precise-signature tests.</summary>
public interface ISigSurface
{
    /// <summary>Does the thing.</summary>
    /// <param name="n">A number.</param>
    int Do(int n);
}

/// <summary>Implements <see cref="ISigSurface" /> — its <c>Do</c> shares the interface slot.</summary>
public class SigSurfaceImpl : ISigSurface
{
    /// <summary>Does the thing.</summary>
    /// <param name="n">A number.</param>
    public int Do(int n) => n;
}

/// <summary>Virtual base for the override / lockstep-note precise-signature tests.</summary>
public class SigBase
{
    public virtual int Calc(int a) => a;
}

/// <summary>Override of <see cref="SigBase.Calc" /> — its up-slot is <c>SigBase.Calc</c>.</summary>
public class SigDerived : SigBase
{
    public override int Calc(int a) => a * 2;
}

/// <summary>Reduced-form extension method for the receiver-touch precise-signature tests.</summary>
public static class SigExtensions
{
    public static string Tag(this ISigSurface s, string t) => $"{s.Do(0)}:{t}";
}

/// <summary>Constructor base for the <c>: base(...)</c> ctor-initializer census test.</summary>
public class SigCtorBase
{
    /// <summary>Builds the base.</summary>
    /// <param name="id">The identifier.</param>
    public SigCtorBase(int id) => Id = id;

    public int Id { get; }
}

/// <summary>Derived ctor that chains <c>: base(id)</c> — a ctor-initializer call site.</summary>
public class SigCtorDerived : SigCtorBase
{
    public SigCtorDerived(int id) : base(id)
    {
    }
}

/// <summary>Overrides a metadata member (<see cref="object.ToString" />) — the metadata-slot guard case.</summary>
public class SigMetaOverride
{
    public override string ToString() => "meta";
}

/// <summary>Isolated consumers of the family members above.</summary>
public class SignatureChangeFamilyConsumer
{
    public int DoViaInterface(ISigSurface s) => s.Do(10);

    public int DoViaConcrete(SigSurfaceImpl impl) => impl.Do(20);

    public int CalcViaDerived() => new SigDerived().Calc(5);

    public int CalcViaBase() => new SigBase().Calc(6);

    public string TagReduced(ISigSurface s) => s.Tag("x");

    public string TagStatic(ISigSurface s) => SigExtensions.Tag(s, "y");

    public int NewBaseCtor() => new SigCtorBase(5).Id;

    public int NewDerivedCtor() => new SigCtorDerived(7).Id;
}
