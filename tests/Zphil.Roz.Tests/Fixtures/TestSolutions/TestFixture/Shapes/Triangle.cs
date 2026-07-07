namespace TestFixture.Shapes;

public class Triangle(double a, double b, double c) : Shape
{
    public override double Area
    {
        get
        {
            double s = (a + b + c) / 2;
            return Math.Sqrt(s * (s - a) * (s - b) * (s - c));
        }
    }

    public override double Perimeter => a + b + c;

    public override string Describe() => $"{base.Describe()} [triangle]";
}
