using Microsoft.Extensions.DependencyInjection;
using TestFixture.Shapes;

namespace TestFixture.Services;

/// <summary>
///     Consumer of the builder extensions in <see cref="ShapeBuilderExtensions" />.
///     Exercises the nested-invocation detection paths in <c>DiRegistrationScanner</c>:
///     lambda-nesting (<see cref="AddShapes" />) and fluent-chain walk
///     (<see cref="UseTracing" /> → <see cref="WithTracing" />).
/// </summary>
public static class DemoRegistration
{
    public static void Configure(IServiceCollection services)
    {
        // Lambda-nesting case: AddShapes matches MEDI directly; AddShape<Triangle> is inside its lambda.
        services.AddShapes(s => s.AddShape<Triangle>());

        // Method-chain case: WithTracing's first param is ITracingBuilder (no direct MEDI match),
        // so detection walks the chain backward to UseTracing, which matches MEDI.
        services.UseTracing().WithTracing(t => t.AddSource<Rectangle>());

        // Add* filter negative case: ConfigureShape<Triangle> starts with "Configure", so the
        // nested-invocation fallback ignores it. Only the AddShape<Triangle> line above should
        // show up as a Triangle registration.
        services.AddShapes(s => s.ConfigureShape<Triangle>());
    }
}
