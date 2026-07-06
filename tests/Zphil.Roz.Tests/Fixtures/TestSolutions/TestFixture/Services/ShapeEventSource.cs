namespace TestFixture.Services;

public class ShapeEventSource
{
    public event EventHandler<string>? ShapeAdded;
    public event EventHandler? ShapeRemoved;

    public void AddShape(string name) => ShapeAdded?.Invoke(this, name);
    public void RemoveShape() => ShapeRemoved?.Invoke(this, EventArgs.Empty);
}
