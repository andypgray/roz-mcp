using TestFixture.Shapes;

// Placed inside the MEDI namespace so MediRecognizer matches the outer builder extensions
// (first parameter IServiceCollection + Microsoft.Extensions.DependencyInjection namespace).
// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Builder surface used by the lambda-nesting registration pattern
///     (<c>services.AddShapes(s =&gt; s.AddShape&lt;T&gt;())</c>).
/// </summary>
public interface IShapeBuilder { }

/// <summary>
///     Builder surface used by the method-chain registration pattern
///     (<c>services.UseTracing().WithTracing(t =&gt; t.AddSource&lt;T&gt;())</c>).
/// </summary>
public interface ITracingBuilder { }

/// <summary>
///     Extension methods exercising the two builder patterns the DI scanner must detect:
///     a lambda-nesting builder (<see cref="AddShapes" />) and a fluent-chain builder
///     rooted on a no-lambda head (<see cref="UseTracing" />).
/// </summary>
public static class ShapeBuilderExtensions
{
    public static IServiceCollection AddShapes(this IServiceCollection services, Action<IShapeBuilder> configure) =>
        services;

    public static IShapeBuilder AddShape<T>(this IShapeBuilder builder) where T : IShape => builder;

    public static IShapeBuilder ConfigureShape<T>(this IShapeBuilder builder) where T : IShape => builder;

    // Chain-head: takes IServiceCollection (MEDI namespace + IServiceCollection first param → MediRecognizer matches).
    public static ITracingBuilder UseTracing(this IServiceCollection services) => null!;

    // Intermediate: first param is ITracingBuilder, so MediRecognizer does NOT match this directly.
    // Detection relies on walking the fluent chain backward from here to UseTracing.
    public static ITracingBuilder WithTracing(this ITracingBuilder builder, Action<ITracingBuilder> configure) =>
        builder;

    public static ITracingBuilder AddSource<T>(this ITracingBuilder builder) where T : IShape => builder;
}
