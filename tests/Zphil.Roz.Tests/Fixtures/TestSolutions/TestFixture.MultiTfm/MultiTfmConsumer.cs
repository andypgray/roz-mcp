namespace TestFixture.MultiTfm;

public class MultiTfmConsumer
{
    private readonly IMultiTfmService _service = new MultiTfmService();

    public string UseService()
    {
        int unused = 42;
        string value = _service.GetValue();
        int result = _service.Calculate(1, 2);
        TestReferenceTarget();
        return $"{value}: {result}";
    }

    public void TestReferenceTarget()
    {
    }
}
