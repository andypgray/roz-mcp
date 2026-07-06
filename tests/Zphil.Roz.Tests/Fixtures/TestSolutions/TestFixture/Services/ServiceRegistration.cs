using Microsoft.Extensions.DependencyInjection;
using TestFixture.Shapes;

namespace TestFixture.Services;

/// <summary>
///     DI registration examples for testing DI-awareness features.
/// </summary>
public static class ServiceRegistration
{
    public static void Configure(IServiceCollection services)
    {
        // Generic interface + implementation
        services.AddScoped<IShape, Circle>();

        // Generic single-type
        services.AddTransient<ShapeService>();

        // Singleton
        services.AddSingleton<ShapeCalculator>();

        // typeof-based registration
        services.AddScoped(typeof(IShape), typeof(Rectangle));

        // Factory lambda
        services.AddScoped<ShapeCollection>(sp => new ShapeCollection());
    }
}
