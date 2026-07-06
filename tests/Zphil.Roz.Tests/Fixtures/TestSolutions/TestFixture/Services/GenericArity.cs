namespace TestFixture.Services;

/// <summary>
///     Non-generic Processor (arity 0).
/// </summary>
public class Processor
{
    public void Run() { }
}

/// <summary>
///     Generic Processor with one type parameter (arity 1).
/// </summary>
public class Processor<T>
{
    public T? Value { get; set; }
    public void Run(T input) { }
}

/// <summary>
///     Generic Processor with two type parameters (arity 2).
/// </summary>
public class Processor<TInput, TOutput>
{
    public TOutput? Process(TInput input) => default;
}

public class BasicProcessor : Processor
{
}

public class StringProcessor : Processor<string>
{
}

public class PairProcessor : Processor<string, int>
{
}

/// <summary>
///     Generic-only type with arity 1 (no non-generic counterpart).
/// </summary>
public class Widget<T>
{
    public T? Item { get; set; }
}

/// <summary>
///     Generic-only type with arity 2 (no non-generic counterpart).
/// </summary>
public class Widget<T1, T2>
{
    public T1? First { get; set; }
    public T2? Second { get; set; }
}

public class StringWidget : Widget<string>
{
}

public class PairWidget : Widget<string, int>
{
}
