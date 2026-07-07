namespace TestFixture.Shapes;

/// <inheritdoc />
public abstract class Shape : IShape
{
    /// <inheritdoc />
    public abstract double Area { get; }

    /// <inheritdoc />
    public abstract double Perimeter { get; }

    /// <inheritdoc />
    public virtual string Describe() =>
        $"{GetType().Name}: Area={Area:F2}, Perimeter={Perimeter:F2}";
}
