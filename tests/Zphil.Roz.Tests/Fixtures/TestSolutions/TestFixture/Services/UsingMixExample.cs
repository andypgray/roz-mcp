using static System.Math;
using SL = System.Collections.Generic.List<int>;
using System;
using System.IO;
using TestFixture.Shapes;

namespace TestFixture.Services;

public class UsingMixExample
{
    public double Compute(double x) => Abs(x);
    public SL CreateList() => new();
    public IShape? CurrentShape { get; set; }
    public string ReadFile(string path) => File.ReadAllText(path);
}
