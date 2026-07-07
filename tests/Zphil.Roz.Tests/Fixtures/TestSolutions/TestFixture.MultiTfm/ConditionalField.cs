namespace TestFixture.MultiTfm;

public class ConditionalField
{
#if NET9_0_OR_GREATER
    private readonly Lock lockObj = new();
#else
    private readonly object lockObj = new();
#endif

    public void DoWork()
    {
        lock (lockObj)
        {
            // work
        }
    }
}
