namespace TestFixture.Services;

// Covers SymbolFormatter.FormatConstraints — HasUnmanagedTypeConstraint branch
public class UnmanagedProcessor<T> where T : unmanaged
{
    public T Value { get; set; }
}

// Covers SymbolFormatter.FormatConstraints — HasValueTypeConstraint (struct) branch
public class StructProcessor<T> where T : struct
{
    public T? NullableValue { get; set; }
}

// Covers SymbolFormatter.FormatConstraints — HasNotNullConstraint branch
public class NotNullProcessor<T> where T : notnull
{
    public T Value { get; set; } = default!;
}

// Covers SymbolFormatter.AppendTypeSpecificDetails — IsAsync branch
public class AsyncService
{
    public async Task<double> CalculateAsync(int value)
    {
        return await Task.FromResult(value * 2.0);
    }
}

// Covers SymbolFormatter.AppendTypeSpecificDetails — property accessor combinations
public class PropertyAccessorExamples
{
    public string ReadOnly { get; } = "";
    public string WriteOnly { set { } }
    public string InitOnly { get; init; } = "";
    public string GetSet { get; set; } = "";
}
