using Zphil.Roz.Symbols;

namespace Zphil.Roz.Tests.Symbols;

public class FqnParserTests
{
    // ── IsFqn ────────────────────────────────────────────────────────────────

    [Theory]
    // Simple names — NOT FQN
    [InlineData("Circle", false)]
    [InlineData("GetArea", false)]
    [InlineData("IShape", false)]
    [InlineData("", false)]
    [InlineData("op_Addition", false)]
    [InlineData("this[]", false)]
    // Special members — NOT FQN (dots but special-cased)
    [InlineData(".ctor", false)]
    [InlineData(".cctor", false)]
    // Dotted names — IS FQN
    [InlineData("Circle.GetArea", true)]
    [InlineData("TestFixture.Shapes.Circle", true)]
    [InlineData("TestFixture.Shapes.Circle.GetArea", true)]
    [InlineData("A.B.C.D.E", true)]
    [InlineData("A.B", true)]
    // Special members with type prefix — IS FQN (type.ctor pattern)
    [InlineData("Circle..ctor", true)]
    [InlineData("ShapeCalculator..cctor", true)]
    [InlineData("TestFixture.Shapes.Circle..ctor", true)]
    // Operators with type prefix — IS FQN
    [InlineData("ShapeCollection.op_Addition", true)]
    [InlineData("ShapeCollection.op_Implicit", true)]
    public void IsFqn_ReturnsExpected(string input, bool expected) => FqnParser.IsFqn(input).ShouldBe(expected);

    // ── ContainsGenericSyntax ─────────────────────────────────────────────────

    [Theory]
    // Angle bracket generics
    [InlineData("List<T>", true)]
    [InlineData("Dictionary<string, int>", true)]
    [InlineData("IGenericRepository<T>", true)]
    [InlineData("TestFixture.Services.ShapeProcessor<T>", true)]
    [InlineData("ShapeProcessor<T>.Describe", true)]
    [InlineData("IEndpoint<TResponse, TRequest>", true)]
    // Backtick arity notation
    [InlineData("List`1", true)]
    [InlineData("Dictionary`2", true)]
    [InlineData("TestFixture.Services.IEndpoint`2", true)]
    [InlineData("IEndpoint`2.Handle", true)]
    // Non-generic names
    [InlineData("Circle", false)]
    [InlineData("Circle.GetArea", false)]
    [InlineData("TestFixture.Shapes.Circle", false)]
    [InlineData(".ctor", false)]
    [InlineData("op_Addition", false)]
    [InlineData("", false)]
    public void ContainsGenericSyntax_ReturnsExpected(string input, bool expected) => FqnParser.ContainsGenericSyntax(input).ShouldBe(expected);

    // ── Decompose ─────────────────────────────────────────────────────────────

    [Theory]
    // Two segments: candidate 1 = (type=A, member=B), candidate 2 = (type=A.B, member=null)
    [InlineData("Circle.GetArea", "Circle", "GetArea", "Circle.GetArea", null)]
    [InlineData("Shape.Area", "Shape", "Area", "Shape.Area", null)]
    [InlineData("IShape.Describe", "IShape", "Describe", "IShape.Describe", null)]
    [InlineData("ShapeColor.Red", "ShapeColor", "Red", "ShapeColor.Red", null)]
    // Three segments
    [InlineData("TestFixture.Shapes.Circle", "TestFixture.Shapes", "Circle", "TestFixture.Shapes.Circle", null)]
    [InlineData("TestFixture.Services.ShapeService", "TestFixture.Services", "ShapeService", "TestFixture.Services.ShapeService", null)]
    // Four segments
    [InlineData("TestFixture.Shapes.Circle.GetArea", "TestFixture.Shapes.Circle", "GetArea", "TestFixture.Shapes.Circle.GetArea", null)]
    [InlineData("TestFixture.Services.ShapeService.Format", "TestFixture.Services.ShapeService", "Format", "TestFixture.Services.ShapeService.Format", null)]
    // Five segments
    [InlineData("A.B.C.D.E", "A.B.C.D", "E", "A.B.C.D.E", null)]
    // Operator as last segment
    [InlineData("ShapeCollection.op_Addition", "ShapeCollection", "op_Addition", "ShapeCollection.op_Addition", null)]
    [InlineData("ShapeCollection.op_Implicit", "ShapeCollection", "op_Implicit", "ShapeCollection.op_Implicit", null)]
    // Finalize (destructor) as last segment
    [InlineData("ShapeCollection.Finalize", "ShapeCollection", "Finalize", "ShapeCollection.Finalize", null)]
    public void Decompose_StandardNames_ReturnsTwoCandidates(
        string input, string type1, string? member1, string type2, string? member2)
    {
        // Act
        IReadOnlyList<FqnCandidate> candidates = FqnParser.Decompose(input);

        // Assert
        candidates.Count.ShouldBe(2);
        candidates[0].TypeFqn.ShouldBe(type1);
        candidates[0].MemberName.ShouldBe(member1);
        candidates[1].TypeFqn.ShouldBe(type2);
        candidates[1].MemberName.ShouldBe(member2);
    }

    [Theory]
    // .ctor with type prefix — only one candidate (always member, never type)
    [InlineData("Circle..ctor", "Circle", ".ctor")]
    [InlineData("ShapeCalculator..cctor", "ShapeCalculator", ".cctor")]
    // Full FQN with .ctor
    [InlineData("TestFixture.Shapes.Circle..ctor", "TestFixture.Shapes.Circle", ".ctor")]
    [InlineData("TestFixture.Services.ShapeCalculator..cctor", "TestFixture.Services.ShapeCalculator", ".cctor")]
    public void Decompose_SpecialMemberSuffix_ReturnsSingleCandidate(
        string input, string expectedType, string expectedMember)
    {
        // Act
        IReadOnlyList<FqnCandidate> candidates = FqnParser.Decompose(input);

        // Assert
        candidates.Count.ShouldBe(1);
        candidates[0].TypeFqn.ShouldBe(expectedType);
        candidates[0].MemberName.ShouldBe(expectedMember);
    }

    // ── SimpleName / Namespace ────────────────────────────────────────────────

    [Theory]
    [InlineData("A.B.C", "C")]
    [InlineData("TestFixture.Shapes.Circle", "Circle")]
    [InlineData("Circle", "Circle")] // undotted — whole string
    [InlineData("", "")]
    public void SimpleName_ReturnsSegmentAfterLastDot(string input, string expected) =>
        FqnParser.SimpleName(input).ShouldBe(expected);

    [Theory]
    [InlineData("A.B.C", "A.B")]
    [InlineData("TestFixture.Shapes.Circle", "TestFixture.Shapes")]
    [InlineData("Circle", "")] // undotted — empty qualifier
    [InlineData("", "")]
    public void Namespace_ReturnsQualifierBeforeLastDot(string input, string expected) =>
        FqnParser.Namespace(input).ShouldBe(expected);
}
