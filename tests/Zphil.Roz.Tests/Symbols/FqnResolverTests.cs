using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Symbols;

/// <summary>
///     Integration tests for FQN resolution through the tool layer.
///     Uses <see cref="NavigationTools.FindSymbol" /> (which flows through NavigationService → FqnResolver)
///     and <see cref="ReferenceTools.FindReferences" /> (which flows through SymbolResolver → FqnResolver).
/// </summary>
public class FqnResolverTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools navigationTools = TestFileHelper.CreateNavigationTools(fixture);
    private readonly ReferenceTools referenceTools = TestFileHelper.CreateReferenceTools(fixture);
    private readonly TypeHierarchyTools typeHierarchyTools = TestFileHelper.CreateTypeTools(fixture);

    // ── Type-only FQN resolution ──────────────────────────────────────────────

    [Theory]
    // Full namespace + class
    [InlineData("TestFixture.Shapes.Circle", "Circle")]
    [InlineData("TestFixture.Shapes.IShape", "IShape")]
    [InlineData("TestFixture.Shapes.Shape", "Shape")]
    [InlineData("TestFixture.Shapes.Square", "Square")]
    [InlineData("TestFixture.Shapes.Rectangle", "Rectangle")]
    [InlineData("TestFixture.Shapes.Triangle", "Triangle")]
    [InlineData("TestFixture.Shapes.Pentagon", "Pentagon")]
    [InlineData("TestFixture.Services.ShapeService", "ShapeService")]
    [InlineData("TestFixture.Services.ShapeCalculator", "ShapeCalculator")]
    [InlineData("TestFixture.Services.AsyncService", "AsyncService")]
    [InlineData("TestFixture.Services.ShapeCollection", "ShapeCollection")]
    [InlineData("TestFixture.Services.ShapeHelper", "ShapeHelper")]
    [InlineData("TestFixture.Services.OuterContainer", "OuterContainer")]
    // Struct
    [InlineData("TestFixture.Services.Point", "Point")]
    // Record
    [InlineData("TestFixture.Services.ShapeSnapshot", "ShapeSnapshot")]
    // Record struct
    [InlineData("TestFixture.Services.ShapeId", "ShapeId")]
    // Enum
    [InlineData("TestFixture.Services.ShapeColor", "ShapeColor")]
    // Delegate
    [InlineData("TestFixture.Services.ShapeMetricFunc", "ShapeMetricFunc")]
    // Legacy namespace (non-file-scoped)
    [InlineData("TestFixture.Legacy.LegacyClass", "LegacyClass")]
    // Minimal namespace
    [InlineData("TestFixture.Minimal.Marker", "Marker")]
    public async Task FindSymbol_TypeByFqn_FindsType(string fqn, string expectedName)
    {
        // Act
        string result = await navigationTools.FindSymbol([fqn], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain(expectedName);
    }

    // ── Type.Member FQN resolution (two segments) ─────────────────────────────

    [Theory]
    // Property on concrete class
    [InlineData("Circle.Area", "Area")]
    [InlineData("Circle.Perimeter", "Perimeter")]
    [InlineData("Circle.Radius", "Radius")]
    // Virtual method
    [InlineData("Shape.Describe", "Describe")]
    // Abstract property
    [InlineData("Shape.Area", "Area")]
    // Interface member
    [InlineData("IShape.Area", "Area")]
    [InlineData("IShape.Describe", "Describe")]
    [InlineData("IShape.Perimeter", "Perimeter")]
    // Static method
    [InlineData("ShapeCalculator.GetDefaultRadius", "GetDefaultRadius")]
    // Static field
    [InlineData("ShapeCalculator.DefaultRadius", "DefaultRadius")]
    // Method on static class
    [InlineData("ShapeHelper.FormatAll", "FormatAll")]
    // Property with various accessors
    [InlineData("PropertyAccessorExamples.ReadOnly", "ReadOnly")]
    [InlineData("PropertyAccessorExamples.WriteOnly", "WriteOnly")]
    [InlineData("PropertyAccessorExamples.InitOnly", "InitOnly")]
    [InlineData("PropertyAccessorExamples.GetSet", "GetSet")]
    // Event
    [InlineData("ShapeEventSource.ShapeAdded", "ShapeAdded")]
    [InlineData("ShapeEventSource.ShapeRemoved", "ShapeRemoved")]
    // Struct member
    [InlineData("Point.DistanceTo", "DistanceTo")]
    [InlineData("Point.X", "X")]
    [InlineData("Point.Y", "Y")]
    // Enum member
    [InlineData("ShapeColor.Red", "Red")]
    [InlineData("ShapeColor.Blue", "Blue")]
    [InlineData("ShapeColor.Green", "Green")]
    [InlineData("ShapeColor.Yellow", "Yellow")]
    // Record property
    [InlineData("ShapeSnapshot.Name", "Name")]
    [InlineData("ShapeSnapshot.Area", "Area")]
    // Async method
    [InlineData("AsyncService.CalculateAsync", "CalculateAsync")]
    // Method with overloads (should match at least one)
    [InlineData("ShapeService.Format", "Format")]
    [InlineData("ShapeService.GetLargest", "GetLargest")]
    [InlineData("ShapeService.ProcessShape", "ProcessShape")]
    public async Task FindSymbol_MemberByTypeDotMember_FindsMember(string fqn, string expectedName)
    {
        // Act
        string result = await navigationTools.FindSymbol([fqn], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain(expectedName);
    }

    // ── Full namespace.type.member FQN resolution ─────────────────────────────

    [Theory]
    [InlineData("TestFixture.Shapes.Circle.Area", "Area")]
    [InlineData("TestFixture.Shapes.Circle.Radius", "Radius")]
    [InlineData("TestFixture.Shapes.IShape.Describe", "Describe")]
    [InlineData("TestFixture.Shapes.Shape.Describe", "Describe")]
    [InlineData("TestFixture.Shapes.Triangle.Describe", "Describe")]
    [InlineData("TestFixture.Services.ShapeService.GetLargest", "GetLargest")]
    [InlineData("TestFixture.Services.ShapeCalculator.DefaultRadius", "DefaultRadius")]
    [InlineData("TestFixture.Services.ShapeCalculator.GetDefaultRadius", "GetDefaultRadius")]
    [InlineData("TestFixture.Services.Point.DistanceTo", "DistanceTo")]
    [InlineData("TestFixture.Services.ShapeColor.Red", "Red")]
    [InlineData("TestFixture.Services.ShapeEventSource.ShapeAdded", "ShapeAdded")]
    [InlineData("TestFixture.Services.AsyncService.CalculateAsync", "CalculateAsync")]
    [InlineData("TestFixture.Services.ShapeHelper.FormatAll", "FormatAll")]
    public async Task FindSymbol_FullFqn_FindsMember(string fqn, string expectedName)
    {
        // Act
        string result = await navigationTools.FindSymbol([fqn], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain(expectedName);
    }

    // ── Special members via FQN ────────────────────────────────────────────────

    [Theory]
    // Instance constructors
    [InlineData("Circle..ctor", "Circle")]
    [InlineData("Pentagon..ctor", "Pentagon")]
    [InlineData("ShapeCalculator..ctor", "ShapeCalculator")]
    // Static constructor
    [InlineData("ShapeCalculator..cctor", "ShapeCalculator")]
    // Full FQN constructors
    [InlineData("TestFixture.Shapes.Circle..ctor", "Circle")]
    [InlineData("TestFixture.Services.ShapeCalculator..cctor", "ShapeCalculator")]
    public async Task FindSymbol_ConstructorByFqn_FindsConstructor(string fqn, string expectedTypeName)
    {
        // Act
        string result = await navigationTools.FindSymbol([fqn], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain(expectedTypeName);
    }

    [Theory]
    // Operator
    [InlineData("ShapeCollection.op_Addition", "op_Addition")]
    [InlineData("ShapeCollection.op_Implicit", "op_Implicit")]
    // Full FQN operator
    [InlineData("TestFixture.Services.ShapeCollection.op_Addition", "op_Addition")]
    public async Task FindSymbol_OperatorByFqn_FindsOperator(string fqn, string expectedName)
    {
        // Act
        string result = await navigationTools.FindSymbol([fqn], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain(expectedName);
    }

    [Fact]
    public async Task FindSymbol_IndexerByFqn_FindsIndexer()
    {
        // Act
        string result = await navigationTools.FindSymbol(["ShapeCollection.this[]"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("this[]");
    }

    [Fact]
    public async Task FindSymbol_FinalizerByFqn_FindsFinalizer()
    {
        // Act
        string result = await navigationTools.FindSymbol(["ShapeCollection.Finalize"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Finalize");
    }

    // ── Nested type resolution ────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_NestedTypeAsMember_FindsNestedType()
    {
        // Act — OuterContainer.InnerProcessor: first tries type=OuterContainer, member=InnerProcessor
        string result = await navigationTools.FindSymbol(["OuterContainer.InnerProcessor"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("InnerProcessor");
    }

    [Fact]
    public async Task FindSymbol_NestedTypeMember_FindsMember()
    {
        // Act — InnerProcessor.Process: type=InnerProcessor, member=Process
        string result = await navigationTools.FindSymbol(["InnerProcessor.Process"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Process");
    }

    // ── Partial class members ─────────────────────────────────────────────────

    [Theory]
    [InlineData("PartialShapeProcessor.ProcessName", "ProcessName")]
    [InlineData("PartialShapeProcessor.ProcessCount", "ProcessCount")]
    [InlineData("PartialShapeProcessor.Reset", "Reset")]
    public async Task FindSymbol_PartialClassMember_FindsMember(string fqn, string expectedName)
    {
        // Act
        string result = await navigationTools.FindSymbol([fqn], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain(expectedName);
    }

    // ── Case insensitivity ────────────────────────────────────────────────────

    [Theory]
    [InlineData("testfixture.shapes.circle", "Circle")]
    [InlineData("TESTFIXTURE.SHAPES.CIRCLE", "Circle")]
    [InlineData("TestFixture.SHAPES.circle", "Circle")]
    public async Task FindSymbol_FqnCaseInsensitive_FindsType(string fqn, string expectedName)
    {
        // Act
        string result = await navigationTools.FindSymbol([fqn], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain(expectedName);
    }

    [Theory]
    [InlineData("circle.area", "Area")]
    [InlineData("CIRCLE.AREA", "Area")]
    [InlineData("Circle.AREA", "Area")]
    public async Task FindSymbol_MemberFqnCaseInsensitive_FindsMember(string fqn, string expectedName)
    {
        // Act
        string result = await navigationTools.FindSymbol([fqn], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain(expectedName);
    }

    // ── FQN through SymbolResolver (find_references referenceKinds=invocations, find_references) ────

    [Theory]
    [InlineData("Circle.Area", "Area")]
    [InlineData("TestFixture.Shapes.Circle.Area", "Area")]
    [InlineData("ShapeService.ProcessShape", "ProcessShape")]
    public async Task FindReferences_Invocations_ByFqn_Works(string fqn, string expectedMember)
    {
        // Act
        string result = await referenceTools.FindReferences(symbolNames: [fqn], referenceKinds: ReferenceKind.Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — FQN resolved to the named member (a failed resolution returns "No symbol found …")
        result.ShouldContain($"'{expectedMember}'");
        result.ShouldNotContain("No symbol found");
    }

    [Theory]
    [InlineData("Circle.Area")]
    [InlineData("TestFixture.Shapes.Circle.Area")]
    public async Task FindReferences_ByFqn_Works(string fqn)
    {
        // Act
        string result = await referenceTools.FindReferences(symbolNames: [fqn], ct: TestContext.Current.CancellationToken);

        // Assert — FQN resolved to Circle.Area (a failed resolution returns "No symbol found …")
        result.ShouldContain("'Area'");
        result.ShouldNotContain("No symbol found");
    }

    [Theory]
    [InlineData("TestFixture.Shapes.Circle")]
    [InlineData("TestFixture.Shapes.Shape")]
    public async Task GetTypeHierarchy_ByFqn_Works(string fqn)
    {
        // Act
        string result = await typeHierarchyTools.GetTypeHierarchy(symbolNames: [fqn], ct: TestContext.Current.CancellationToken);

        // Assert — FQN resolved to a real type: both Circle and Shape hierarchies surface IShape
        // (a failed resolution returns "No symbol found …").
        result.ShouldContain("IShape");
        result.ShouldNotContain("No symbol found");
    }

    // ── Generic FQN rejection ─────────────────────────────────────────────────

    [Theory]
    [InlineData("ShapeProcessor<T>.Describe")]
    [InlineData("IGenericRepository<T>.GetById")]
    [InlineData("TestFixture.Services.ShapeProcessor<T>")]
    [InlineData("List<T>")]
    [InlineData("IEndpoint`2.Handle")]
    [InlineData("Dictionary<string, int>")]
    public async Task FindSymbol_GenericFqn_ReturnsHelpfulError(string fqn)
    {
        // Act — per-name error is captured inline.
        // Bare backtick arity (e.g. "List`1") is accepted as open-generic syntax —
        // only concrete generic args and dotted-FQN-plus-backtick combinations error here.
        string result = await navigationTools.FindSymbol([fqn], ct: TestContext.Current.CancellationToken);

        result.ShouldContain("Generic FQN syntax");
        result.ShouldContain("not supported");
    }

    [Theory]
    [InlineData("ShapeProcessor<T>.Describe")]
    [InlineData("IEndpoint`2.Handle")]
    public async Task FindReferences_Invocations_GenericFqn_ReturnsHelpfulError(string fqn)
    {
        // Act — per-name error is captured inline
        string result = await referenceTools.FindReferences(symbolNames: [fqn], referenceKinds: ReferenceKind.Invocations, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("Generic FQN syntax");
    }

    // ── FQN + containingType conflict ─────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_FqnWithContainingType_ReturnsError()
    {
        // Act — per-name error is captured inline
        string result = await navigationTools.FindSymbol(["Circle.GetArea"], containingType: "Circle", ct: TestContext.Current.CancellationToken);

        result.ShouldContain("fully-qualified name");
        result.ShouldContain("Do not combine with containingType");
    }

    [Fact]
    public async Task FindReferences_Invocations_FqnWithContainingType_ReturnsError()
    {
        // Act — per-name error is captured inline
        string result = await referenceTools.FindReferences(symbolNames: ["Circle.Area"], containingType: "Circle", referenceKinds: ReferenceKind.Invocations, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("fully-qualified name");
    }

    // ── No-match fallthrough ──────────────────────────────────────────────────

    [Theory]
    [InlineData("TestFixture.Shapes.NonExistent")]
    [InlineData("Circle.NonExistentMethod")]
    [InlineData("NonExistent.Namespace.Type")]
    [InlineData("A.B.C.D.E.F.G")]
    public async Task FindSymbol_FqnNoMatch_ProducesError(string fqn)
    {
        // Act — FQN doesn't match, falls through to standard search which also fails
        // The result should be a "0 matches" response (not an exception)
        string result = await navigationTools.FindSymbol([fqn], ct: TestContext.Current.CancellationToken);

        // Assert — find_symbol returns "No symbols found" for unrecognized names
        result.ShouldContain("No symbols found");
    }
}
