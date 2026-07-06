namespace TestFixture.MultiTfm;

public class MultiTfmService : IMultiTfmService
{
    public string GetValue() => "hello";

    public int Calculate(int x, int y) => x + y;
}
