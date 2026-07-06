namespace TestFixture.Shapes;

public class Rectangle(double width, double height) : Shape
{
    public double Width { get; } = width;
    public double Height { get; } = height;

    public override double Area => Width * Height;
    public override double Perimeter => 2 * (Width + Height);
}
