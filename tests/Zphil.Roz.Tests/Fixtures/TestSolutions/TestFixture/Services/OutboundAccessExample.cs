namespace TestFixture.Services;

/// <summary>Fixture for analyze_method outbound extraction of implicit-this and null-conditional
/// property accesses (F13). Read() touches a bare property (this.Size), a ?. property
/// (collection?.Count), and a nameof operand that must stay uncounted.</summary>
public class OutboundAccessExample
{
    private int Size => 0;
    private int Untouched => 1;

    public int Read(ShapeCollection? collection)
    {
        int a = Size;                    // bare identifier  -> this.Size            (F13: now counted)
        int b = collection?.Count ?? 0;  // member binding   -> ShapeCollection.Count (F13: now counted)
        _ = nameof(Untouched);           // nameof operand   -> must NOT be counted
        return a + b;
    }

    public void ConditionalCall(ShapeCollection? collection)
    {
        collection?.Dispose();           // ?. invocation -> counted once via the invocation, not again via the binding
    }
}
