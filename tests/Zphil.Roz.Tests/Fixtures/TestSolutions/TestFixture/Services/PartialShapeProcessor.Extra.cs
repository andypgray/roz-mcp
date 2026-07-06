namespace TestFixture.Services;

public partial class PartialShapeProcessor
{
    public void Reset() => ProcessCount = 0;

    public string GeneratedMethod(string label) => $"{label}: generated";
}
