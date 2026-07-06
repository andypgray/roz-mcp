using TestFixture.Shapes;

namespace TestFixture.Services;

public class ShapeCollection : IDisposable
{
    private readonly List<IShape> _shapes = [];
    private bool _disposed;

    public IShape this[int index] => _shapes[index];

    public static ShapeCollection operator +(ShapeCollection left, ShapeCollection right)
    {
        var result = new ShapeCollection();
        result._shapes.AddRange(left._shapes);
        result._shapes.AddRange(right._shapes);
        return result;
    }

    public static implicit operator int(ShapeCollection collection) => collection.Count;

    public static implicit operator string(ShapeCollection collection) => collection.Count.ToString();

    ~ShapeCollection()
    {
        Dispose(false);
    }

    public int Count => _shapes.Count;

    public static void SplitCount(in ShapeCollection source, ref int even, out int odd)
    {
        even = source.Count / 2;
        odd = source.Count - even;
    }

    public void Add(IShape shape) => _shapes.Add(shape);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
                _shapes.Clear();
            _disposed = true;
        }
    }
}
