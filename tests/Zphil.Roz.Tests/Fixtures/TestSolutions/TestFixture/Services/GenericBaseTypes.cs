using TestFixture.Shapes;

namespace TestFixture.Services;

public interface IEndpoint<TResponse, TRequest>
{
    TResponse Handle(TRequest request);
}

public interface IResult
{
    bool Success { get; }
}

public class ShapeResult : IResult
{
    public bool Success { get; init; }
    public IShape? MatchedShape { get; init; }
}

public class ShapeRequest
{
    public string ShapeName { get; init; } = "";
}

public class ShapeEndpoint : IEndpoint<ShapeResult, ShapeRequest>
{
    public ShapeResult Handle(ShapeRequest request) =>
        new() { Success = true };
}
