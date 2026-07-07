namespace TestFixture.Services;

public ref struct ShapeSpan
{
    private readonly Span<int> _areas;

    public ShapeSpan(Span<int> areas) => _areas = areas;

    public int Length => _areas.Length;

    public int this[int index] => _areas[index];
}
