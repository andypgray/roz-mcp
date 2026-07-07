namespace TestFixture.MultiTfm;

public abstract class MultiTfmBase
{
    public abstract string Name { get; }

    public virtual int Priority => 0;
}
