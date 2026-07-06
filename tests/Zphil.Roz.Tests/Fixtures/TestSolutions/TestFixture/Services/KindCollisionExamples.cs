namespace TestFixture.Services;

/// <summary>
///     Deliberate name collisions for testing kind-based disambiguation.
///     "Metric" exists as both a class and an interface.
///     "Measure" exists as both a class and a method on MetricConsumer.
/// </summary>
public interface Metric
{
    double Value { get; }
}

public class Metric<T> : Metric
{
    public T Tag { get; set; } = default!;
    public double Value { get; set; }
}

public class MetricConsumer
{
    public Metric? CurrentMetric { get; set; }

    public double Measure() => CurrentMetric?.Value ?? 0;

    public void Measure(string label)
    {
    }
}
