using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Symbols;

public class LineLevelResolutionTests(WorkspaceFixture fixture)
{
    private readonly ReferenceTools referenceTools = CreateReferenceTools(fixture);
    private readonly TypeHierarchyTools typeHierarchyTools = CreateTypeTools(fixture);

    [Fact]
    public async Task FindReferences_LineOnly_InterfaceDeclaration_ResolvesInterface()
    {
        // Arrange — "public interface IShape" at line 6 in IShape.cs
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — column omitted, line-level resolution finds declared symbol
        string result = await referenceTools.FindReferences([Loc(filePath, 6)], ct: TestContext.Current.CancellationToken);

        // Assert — resolves to IShape and finds references
        result.ShouldContain("IShape");
        result.ShouldContain("location");
    }

    [Fact]
    public async Task GetTypeHierarchy_LineOnly_ClassDeclaration_ResolvesClass()
    {
        // Arrange — "public class Circle(...) : Shape" at line 3 in Circle.cs
        string filePath = fixture.ShapesFile("Circle.cs");

        // Act — column omitted, line-level resolution finds declared symbol
        string result = await typeHierarchyTools.GetTypeHierarchy([Loc(filePath, 3)], ct: TestContext.Current.CancellationToken);

        // Assert — resolves to Circle and shows hierarchy
        result.ShouldContain("Circle");
        result.ShouldContain("Shape");
        result.ShouldContain("IShape");
    }

    [Fact]
    public async Task FindReferences_LineOnly_MethodWithReturnType_ResolvesMethod()
    {
        // Arrange — "public IShape GetLargest(IEnumerable<IShape> shapes) =>" at line 24 in ShapeService.cs
        // Column 1 would land on 'p' of 'public', snap-to-nearest would find IShape return type.
        // Line-level resolution should find GetLargest (the declared member).
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act — column omitted
        string result = await referenceTools.FindReferences([Loc(filePath, 24)], ct: TestContext.Current.CancellationToken);

        // Assert — resolves to GetLargest, not the IShape return type. The quoted header name
        // discriminates: a wrong IShape resolution echoes the unquoted "GetLargest" declaration
        // line but quotes 'IShape', never 'GetLargest'.
        result.ShouldContain("'GetLargest'");
        result.ShouldNotContain("'IShape'");
    }

    [Fact]
    public async Task FindReferences_LineOnly_PropertyDeclaration_ResolvesProperty()
    {
        // Arrange — "double Area { get; }" at line 9 in IShape.cs
        // Column 1 would land on whitespace/double, but line-level should find Area.
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — column omitted
        string result = await referenceTools.FindReferences([Loc(filePath, 9)], ct: TestContext.Current.CancellationToken);

        // Assert — resolves to Area property (quoted header name; a wrong 'double' type
        // resolution would not quote 'Area').
        result.ShouldContain("'Area'");
    }

    [Fact]
    public async Task FindReferences_LineOnly_ConstructorDeclaration_ResolvesConstructor()
    {
        // Arrange — "public ShapeCalculator(IShape shape)" at line 15 in ShapeCalculator.cs
        string filePath = fixture.ServicesFile("ShapeCalculator.cs");

        // Act — column omitted, should resolve to the .ctor, not the IShape parameter type
        string result = await referenceTools.FindReferences([Loc(filePath, 15)], ct: TestContext.Current.CancellationToken);

        // Assert — resolves to the constructor
        result.ShouldContain(".ctor");
    }

    [Fact]
    public async Task FindReferences_LineOnly_FieldDeclaration_ResolvesField()
    {
        // Arrange — "private static readonly double DefaultRadius;" at line 7 in ShapeCalculator.cs
        string filePath = fixture.ServicesFile("ShapeCalculator.cs");

        // Act — column omitted
        string result = await referenceTools.FindReferences([Loc(filePath, 7)], ct: TestContext.Current.CancellationToken);

        // Assert — resolves to DefaultRadius field (quoted header name; a wrong 'double' type
        // resolution would not quote 'DefaultRadius').
        result.ShouldContain("'DefaultRadius'");
    }

    [Fact]
    public async Task FindReferences_LineOnly_InsideMethodBody_FallsBackToSnapToNearest()
    {
        // Arrange — "_shape = shape;" at line 17 in ShapeCalculator.cs (inside constructor body)
        // No declaration on this line, so line-level returns null → falls back to column 1 snap-to-nearest.
        string filePath = fixture.ServicesFile("ShapeCalculator.cs");

        // Act — column omitted, falls back
        string result = await referenceTools.FindReferences([Loc(filePath, 17)], ct: TestContext.Current.CancellationToken);

        // Assert — snap-to-nearest from inside constructor body should resolve _shape field
        result.ShouldContain("_shape");
    }

    [Fact]
    public async Task FindReferences_ExplicitColumn_OnReturnType_SnapsToMethod()
    {
        // Arrange — "public IShape GetLargest(...)" at line 24 in ShapeService.cs
        // Column on the return type should snap to the declared method in non-strict mode.
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act — explicit column 12 targets "IShape" return type
        string result = await referenceTools.FindReferences([Loc(filePath, 24, 12)], ct: TestContext.Current.CancellationToken);

        // Assert — snaps to GetLargest (the declared method), not the IShape return type
        result.ShouldContain("'GetLargest'");
        result.ShouldNotContain("'IShape'");
    }

    [Fact]
    public async Task FindReferences_ExplicitColumn_OnPropertyType_SnapsToProperty()
    {
        // Arrange — "double Area { get; }" at line 9 in IShape.cs
        // Column on "double" (the property type) should snap to Area.
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — column 5 targets "double" type
        string result = await referenceTools.FindReferences([Loc(filePath, 9, 5)], ct: TestContext.Current.CancellationToken);

        // Assert — snaps to Area property (quoted header name; not the 'double' type)
        result.ShouldContain("'Area'");
    }

    [Fact]
    public async Task FindReferences_ExplicitColumn_OnInterfaceMethodReturnType_SnapsToMethod()
    {
        // Arrange — "string Describe();" at line 18 in IShape.cs
        // Column on "string" (return type) should snap to Describe.
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — column 5 targets "string" return type
        string result = await referenceTools.FindReferences([Loc(filePath, 18, 5)], ct: TestContext.Current.CancellationToken);

        // Assert — snaps to Describe method (quoted header name; not the 'string' return type)
        result.ShouldContain("'Describe'");
    }

    [Fact]
    public async Task FindReferences_ExplicitColumn_OnParameterType_ResolvesToType()
    {
        // Arrange — "public IShape GetLargest(IEnumerable<IShape> shapes) =>" at line 24
        // Column on "IShape" inside the parameter list should resolve to the type, not the method.
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act — column 44 targets "IShape" (cols 42-47) inside the generic parameter type
        // IEnumerable<IShape>. Col 40 is the trailing 'e' of IEnumerable, not IShape.
        string result = await referenceTools.FindReferences([Loc(filePath, 24, 44)], ct: TestContext.Current.CancellationToken);

        // Assert — resolves to IShape (the parameter type), not the GetLargest method. The quoted
        // header name discriminates: a wrong GetLargest resolution would quote 'GetLargest'.
        result.ShouldContain("'IShape'");
        result.ShouldNotContain("'GetLargest'");
    }

    [Fact]
    public async Task FindReferences_ExplicitColumn_OnOperatorReturnType_SnapsToOperator()
    {
        // Arrange — "public static ShapeCollection operator +(...)" at line 12 in ShapeCollection.cs
        // Column on "ShapeCollection" (return type) should snap to op_Addition.
        string filePath = fixture.ServicesFile("ShapeCollection.cs");

        // Act — column 19 targets "ShapeCollection" return type
        string result = await referenceTools.FindReferences([Loc(filePath, 12, 19)], ct: TestContext.Current.CancellationToken);

        // Assert — snaps to op_Addition
        result.ShouldContain("op_Addition");
    }

    [Fact]
    public async Task FindReferences_ExplicitColumn_OnIndexerReturnType_SnapsToIndexer()
    {
        // Arrange — "public IShape this[int index] => _shapes[index];" at line 10 in ShapeCollection.cs
        // Column on "IShape" (return type) should snap to the indexer.
        string filePath = fixture.ServicesFile("ShapeCollection.cs");

        // Act — column 12 targets "IShape" return type
        string result = await referenceTools.FindReferences([Loc(filePath, 10, 12)], ct: TestContext.Current.CancellationToken);

        // Assert — snaps to the indexer (this[])
        result.ShouldContain("this[]");
    }

    [Fact]
    public async Task FindReferences_ExplicitColumn_OnFieldType_SnapsToField()
    {
        // Arrange — "    private static readonly double DefaultRadius;" at line 7 in ShapeCalculator.cs
        // Column on "double" (the field type) should snap to DefaultRadius.
        string filePath = fixture.ServicesFile("ShapeCalculator.cs");

        // Act — column 30 targets "double" type
        string result = await referenceTools.FindReferences([Loc(filePath, 7, 30)], ct: TestContext.Current.CancellationToken);

        // Assert — snaps to DefaultRadius field (quoted header name; not the 'double' type)
        result.ShouldContain("'DefaultRadius'");
    }

    [Fact]
    public async Task FindReferences_LineOnly_IndexerDeclaration_ResolvesIndexer()
    {
        // Arrange — "public IShape this[int index] => _shapes[index];" at line 10 in ShapeCollection.cs
        string filePath = fixture.ServicesFile("ShapeCollection.cs");

        // Act — column omitted
        string result = await referenceTools.FindReferences([Loc(filePath, 10)], ct: TestContext.Current.CancellationToken);

        // Assert — resolves to the indexer (this[])
        result.ShouldContain("this[]");
    }

    [Fact]
    public async Task FindReferences_LineOnly_OperatorDeclaration_ResolvesOperator()
    {
        // Arrange — "public static ShapeCollection operator +(...)" at line 12 in ShapeCollection.cs
        string filePath = fixture.ServicesFile("ShapeCollection.cs");

        // Act — column omitted
        string result = await referenceTools.FindReferences([Loc(filePath, 12)], ct: TestContext.Current.CancellationToken);

        // Assert — resolves to op_Addition
        result.ShouldContain("op_Addition");
    }

    [Fact]
    public async Task FindReferences_LineOnly_ConversionOperator_ResolvesConversion()
    {
        // Arrange — "public static implicit operator int(ShapeCollection collection)" at line 20
        string filePath = fixture.ServicesFile("ShapeCollection.cs");

        // Act — column omitted
        string result = await referenceTools.FindReferences([Loc(filePath, 20)], ct: TestContext.Current.CancellationToken);

        // Assert — resolves to op_Implicit
        result.ShouldContain("op_Implicit");
    }

    [Fact]
    public async Task FindReferences_LineOnly_DestructorDeclaration_ResolvesDestructor()
    {
        // Arrange — "~ShapeCollection()" at line 24 in ShapeCollection.cs
        string filePath = fixture.ServicesFile("ShapeCollection.cs");

        // Act — column omitted
        string result = await referenceTools.FindReferences([Loc(filePath, 24)], ct: TestContext.Current.CancellationToken);

        // Assert — resolves to Finalize (destructor)
        result.ShouldContain("Finalize");
    }
}
