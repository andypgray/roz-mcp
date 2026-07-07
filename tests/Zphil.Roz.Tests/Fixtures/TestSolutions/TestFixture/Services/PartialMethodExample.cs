using TestFixture.Shapes;

namespace TestFixture.Services;

// Exercises analyze_method's partial-method body selection: the defining declaration is bodyless,
// so OutboundCallExtractor must walk past it to the implementing part to find the outbound calls.
public partial class PartialMethodExample
{
    public partial string Summarize(IShape shape);
}

public partial class PartialMethodExample
{
    public partial string Summarize(IShape shape) => shape.Describe();
}
