namespace TestFixture.Shapes;

public class Pentagon : Shape
{
    private readonly double side;

    public Pentagon(double side)
    {
        this.side = side;
    }

    public override double Area => 0.25 * Math.Sqrt(5 * (5 + 2 * Math.Sqrt(5))) * side * side;

    public override double Perimeter => 5 * side;
}
