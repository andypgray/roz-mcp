namespace TestFixture.Services;

/// <summary>Fixture for analyze_change_impact SignatureChange on a method referenced as a method
/// group and via nameof (F11): the method-group conversion must be counted; nameof stays dropped.</summary>
public class MethodGroupSurface
{
    public int Callback(int value) => value;
    public Func<int, int> AsDelegate() => Callback; // method-group conversion (non-invocation)
    public string CallbackName() => nameof(Callback); // nameof — must NOT be counted
}
