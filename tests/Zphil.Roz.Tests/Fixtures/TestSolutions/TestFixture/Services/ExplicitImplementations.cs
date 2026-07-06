namespace TestFixture.Services;

public interface IResettable
{
    void Reset();
    int ResetCount { get; }
    event EventHandler? Resetting;
}

public class ShapeManager : IResettable, IDisposable
{
    private int _resetCount;
    private bool _disposed;

    void IResettable.Reset()
    {
        _resetCount++;
    }

    int IResettable.ResetCount => _resetCount;

    event EventHandler? IResettable.Resetting
    {
        add { }
        remove { }
    }

    void IDisposable.Dispose()
    {
        _disposed = true;
    }

    public bool IsDisposed => _disposed;
}
