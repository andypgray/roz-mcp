using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.NavigationToolTests;

public class GoToDefinitionTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools tools = CreateNavigationTools(fixture);

    // ── includeBody / member summary suppression ────────────────────────────

    [Fact]
    public async Task GoToDefinition_UsageSiteWithIncludeBody_SuppressesMemberSummary()
    {
        // Arrange — Circle.cs line 3: `: Shape` — cursor on Shape (usage site navigates to Shape class)
        string filePath = fixture.ShapesFile("Circle.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 3, 39), true, ct: TestContext.Current.CancellationToken);

        // Assert — body is shown but member summary is suppressed
        result.ShouldContain("Body:");
        result.ShouldNotContain("Members (");
    }

    [Fact]
    public async Task GoToDefinition_UsageSiteWithoutIncludeBody_ShowsMemberSummary()
    {
        // Arrange — Circle.cs line 3: `: Shape` — cursor on Shape (usage site navigates to Shape class)
        string filePath = fixture.ShapesFile("Circle.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 3, 39), ct: TestContext.Current.CancellationToken);

        // Assert — member summary visible, no body
        result.ShouldContain("Members (");
        result.ShouldNotContain("Body:");
    }

    // ── Error cases ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GoToDefinition_NonExistentFile_ThrowsInvalidOperation()
    {
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.GoToDefinition(Loc("NonExistent.cs", 1, 1)));

        ex.Message.ShouldContain("File not found in solution");
    }

    [Fact]
    public async Task GoToDefinition_InvalidLine_ThrowsArgumentOutOfRange()
    {
        string filePath = fixture.ShapesFile("Circle.cs");

        await Should.ThrowAsync<UserErrorException>(() => tools.GoToDefinition(Loc(filePath, 999, 1)));
    }

    [Fact]
    public async Task GoToDefinition_LineZero_ThrowsWithMessage()
    {
        string filePath = fixture.ShapesFile("Circle.cs");

        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.GoToDefinition($"{filePath}:0:1"));

        ex.Message.ShouldContain("location line must be positive");
    }

    [Fact]
    public async Task GoToDefinition_ColumnZero_ThrowsWithMessage()
    {
        string filePath = fixture.ShapesFile("Circle.cs");

        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.GoToDefinition($"{filePath}:1:0"));

        ex.Message.ShouldContain("location column must be positive");
    }

    [Fact]
    public async Task GoToDefinition_ColumnPastEndOfLine_ClampsToLineEnd()
    {
        // Line 3 is "public class Circle(double radius) : Shape" — column 99999 is past the end.
        // Read-only tools are forgiving: column is clamped to line length, resolving to
        // the nearest symbol at the end of the line (Shape base class).
        string filePath = fixture.ShapesFile("Circle.cs");

        string result = await tools.GoToDefinition(Loc(filePath, 3, 99999), ct: TestContext.Current.CancellationToken);

        // Clamped position resolves to the Shape base class reference
        result.ShouldContain("Shape");
    }

    // ── Go-to-definition from usage sites ───────────────────────────────────

    [Fact]
    public async Task GoToDefinition_OnMethodCall_ResolvesToDefinition()
    {
        // Arrange — ShapeService.cs line 17: `$"Processing: {shape.Describe()}"` — cursor on Describe
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 17, 30), ct: TestContext.Current.CancellationToken);

        // Assert — resolves to IShape.Describe, located in Shapes/IShape.cs
        result.ShouldContain("Describe");
        result.ShouldContain("method");
        result.ShouldContain("IShape.cs");
    }

    [Fact]
    public async Task GoToDefinition_OnPropertyAccess_ResolvesToDefinition()
    {
        // Arrange — ShapeService.cs line 25: `shapes.MaxBy(s => s.Area)!` — cursor on Area
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 25, 29), ct: TestContext.Current.CancellationToken);

        // Assert — resolves to IShape.Area, located in Shapes/IShape.cs
        result.ShouldContain("Area");
        result.ShouldContain("property");
        result.ShouldContain("IShape.cs");
    }

    [Fact]
    public async Task GoToDefinition_OnTypeReference_ResolvesToDefinition()
    {
        // Arrange — ShapeService.cs line 16: `public string ProcessShape(IShape shape)` — cursor on IShape
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 16, 35), ct: TestContext.Current.CancellationToken);

        // Assert — resolves to IShape interface, located in Shapes/IShape.cs
        result.ShouldContain("IShape");
        result.ShouldContain("interface");
        result.ShouldContain("IShape.cs");
    }

    [Fact]
    public async Task GoToDefinition_OnBaseType_ResolvesToDefinition()
    {
        // Arrange — Circle.cs line 3: `public class Circle(double radius) : Shape` — cursor on Shape
        string filePath = fixture.ShapesFile("Circle.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 3, 39), ct: TestContext.Current.CancellationToken);

        // Assert — resolves to Shape abstract class, located in Shapes/Shape.cs
        result.ShouldContain("Shape");
        result.ShouldContain("class");
        result.ShouldContain("Shape.cs");
    }

    // ── Constructor invocation ─────────────────────────────────────────────

    [Fact]
    public async Task GoToDefinition_OnConstructorInvocation_ResolvesToConstructor()
    {
        // Arrange — ShapeCalculator.cs line 22: `_shape = new Circle(radius);` — cursor on Circle
        string filePath = fixture.ServicesFile("ShapeCalculator.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 22, 22), ct: TestContext.Current.CancellationToken);

        // Assert — resolves to Circle's constructor (primary constructor), located in Circle.cs
        result.ShouldContain("Circle");
        result.ShouldContain("Circle.cs");
    }

    // ── Usage sites should NOT include "at declaration" ────────────────────

    [Theory]
    [InlineData(17, 30)] // method call: shape.Describe()
    [InlineData(25, 29)] // property access: s.Area
    [InlineData(16, 35)] // type reference: IShape
    public async Task GoToDefinition_OnUsageSite_DoesNotIncludeAtDeclarationNote(int line, int column)
    {
        // Arrange — ShapeService.cs usage sites
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, line, column), ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotContain("at declaration");
    }

    // ── Parameter resolution ──────────────────────────────────────────────

    [Fact]
    public async Task GoToDefinition_OnParameterName_ResolvesToDeclaration()
    {
        // Arrange — Circle.cs line 3: `public class Circle(double radius) : Shape` — cursor on 'radius'
        string filePath = fixture.ShapesFile("Circle.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 3, 28), ct: TestContext.Current.CancellationToken);

        // Assert — cursor is already at the parameter declaration, but still returns symbol info
        result.ShouldContain("radius");
        result.ShouldContain("At declaration");
    }

    [Fact]
    public async Task GoToDefinition_OnBlankLine_ResolvesToEnclosingType()
    {
        // Arrange — Circle.cs line 6 is a blank line inside the class body
        string filePath = fixture.ShapesFile("Circle.cs");

        // Act — blank line inside class resolves to the enclosing type
        string result = await tools.GoToDefinition(Loc(filePath, 6, 1), ct: TestContext.Current.CancellationToken);

        // Assert — resolves to Circle class
        result.ShouldContain("Circle");
    }

    [Fact]
    public async Task GoToDefinition_OnCommentInsideClass_ReturnsNoSymbolFound()
    {
        // Arrange — Shape.cs line 12: "    /// <inheritdoc />" — cursor inside a doc comment
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act & Assert — should throw, not return "at declaration"
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.GoToDefinition(Loc(filePath, 12, 10)));

        ex.Message.ShouldContain("in a doc comment");
    }

    // ── project / assembly labels ─────────────────────────────────────────

    [Fact]
    public async Task GoToDefinition_OnSourceSymbol_ShowsProjectName()
    {
        // Arrange — ShapeService.cs line 16: `ProcessShape(IShape shape)` — cursor on IShape
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 16, 35), ct: TestContext.Current.CancellationToken);

        // Assert — should show the project name for source symbols
        result.ShouldContain("Project: TestFixture");
    }

    [Fact]
    public async Task GoToDefinition_OnMetadataType_ShowsAssemblyName()
    {
        // Arrange — ShapeService.cs line 24: `public IShape GetLargest(IEnumerable<IShape> shapes)`
        // cursor on IEnumerable — a metadata-only type from System.Runtime
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 24, 30), ct: TestContext.Current.CancellationToken);

        // Assert — should show the assembly name for metadata-only symbols
        result.ShouldContain("Assembly:");
        result.ShouldNotContain("Project:");
    }

    [Theory]
    [InlineData(40, "ShapeResult")] // first generic arg
    [InlineData(53, "ShapeRequest")] // second generic arg
    public async Task GoToDefinition_OnGenericBaseTypeArg_ResolvesToType(int column, string expectedType)
    {
        // Arrange — GenericBaseTypes.cs line 26:
        // `public class ShapeEndpoint : IEndpoint<ShapeResult, ShapeRequest>`
        string filePath = fixture.ServicesFile("GenericBaseTypes.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 26, column), ct: TestContext.Current.CancellationToken);

        // Assert — resolves to the specific type arg, not ShapeEndpoint
        result.ShouldContain(expectedType);
        result.ShouldNotContain("ShapeEndpoint");
    }

    [Fact]
    public async Task GoToDefinition_OnGenericBaseTypeName_ResolvesToInterface()
    {
        // Arrange — GenericBaseTypes.cs line 26:
        // `public class ShapeEndpoint : IEndpoint<ShapeResult, ShapeRequest>`
        // cursor on IEndpoint (column 30)
        string filePath = fixture.ServicesFile("GenericBaseTypes.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 26, 30), ct: TestContext.Current.CancellationToken);

        // Assert — resolves to IEndpoint interface
        result.ShouldContain("IEndpoint");
        result.ShouldContain("interface");
    }

    [Fact]
    public async Task GoToDefinition_OnSimpleBaseType_ResolvesToInterface()
    {
        // Arrange — GenericBaseTypes.cs line 15:
        // `public class ShapeResult : IResult`
        // cursor on IResult (column 28)
        string filePath = fixture.ServicesFile("GenericBaseTypes.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 15, 28), ct: TestContext.Current.CancellationToken);

        // Assert — resolves to IResult interface, not ShapeResult
        result.ShouldContain("IResult");
        result.ShouldNotContain("ShapeResult");
    }

    // ── Terse output when already at declaration ────────────────────────────

    [Fact]
    public async Task GoToDefinition_AtTypeDeclaration_ShowsMemberListing()
    {
        // Arrange — IShape.cs line 6: `public interface IShape` — cursor on IShape (at declaration)
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 6, 18), ct: TestContext.Current.CancellationToken);

        // Assert — at declaration for a type auto-shows depth=1 member listing
        result.ShouldContain("At declaration");
        result.ShouldContain("Members (3):");
        result.ShouldContain("[public abstract property]");
        result.ShouldContain("[public abstract method]");
        result.ShouldNotContain("get_symbols_overview");
    }

    [Fact]
    public async Task GoToDefinition_AtTypeDeclaration_WithIncludeBody_ReturnsFullOutput()
    {
        // Arrange — IShape.cs line 6: `public interface IShape` — cursor on IShape (at declaration)
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — includeBody=true explicitly requests full detail
        string result = await tools.GoToDefinition(Loc(filePath, 6, 18), true, ct: TestContext.Current.CancellationToken);

        // Assert — full output with body and declaration note, no terse hint
        result.ShouldContain("At declaration.");
        result.ShouldContain("double Area");
        result.ShouldNotContain("get_symbols_overview");
    }

    [Fact]
    public async Task GoToDefinition_AtNonTypeDeclaration_NoMemberHint()
    {
        // Arrange — Circle.cs line 3: `public class Circle(double radius) : Shape` — cursor on 'radius'
        string filePath = fixture.ShapesFile("Circle.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 3, 28), ct: TestContext.Current.CancellationToken);

        // Assert — terse output for parameter, but no member hint (not a type)
        result.ShouldContain("At declaration");
        result.ShouldContain("radius");
        result.ShouldNotContain("get_symbols_overview");
    }

    // ── Extension-method reduced form unwrap ────────────────────────────────

    [Fact]
    public async Task GoToDefinition_OnExtensionMethodCall_PreservesThisParameterAndStaticModifier()
    {
        // Arrange — ShapeService.cs line 41: `shape.Label("S")` — cursor on 'L' of 'Label' (column 60)
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 41, 60), ct: TestContext.Current.CancellationToken);

        // Assert — original declaration form is shown (not the reduced form)
        result.ShouldContain("this IShape shape");
        result.ShouldContain("static");
        result.ShouldContain("Label");
    }

    [Fact]
    public async Task GoToDefinition_OnGenericExtensionMethodCall_ShowsUnsubstitutedOriginalForm()
    {
        // Arrange — ShapeService.cs line 43: `c.WithDescription("tagged")` — cursor on 'W' of 'WithDescription' (column 44)
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 43, 44), ct: TestContext.Current.CancellationToken);

        // Assert — receiver param preserved with 'this T' (not substituted to Circle, not dropped)
        result.ShouldContain("this T shape");
        // Generic type parameter kept (not substituted)
        result.ShouldContain("<T>");
        result.ShouldContain("where T : IShape");
        result.ShouldContain("static");
    }

    // ── Line-only resolution (no column) ────────────────────────────────────

    [Fact]
    public async Task GoToDefinition_LineOnly_MethodDeclaration_ResolvesMethod()
    {
        // Arrange — "public IShape GetLargest(IEnumerable<IShape> shapes) =>" at line 24 in ShapeService.cs
        // Column 1 would land on whitespace before 'public'; line-level should find GetLargest.
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act — column omitted
        string result = await tools.GoToDefinition(Loc(filePath, 24), ct: TestContext.Current.CancellationToken);

        // Assert — resolves to the GetLargest method declaration, not its IShape return type
        result.ShouldContain("GetLargest");
        result.ShouldContain("method");
    }

    [Fact]
    public async Task GoToDefinition_LineOnly_InsideMethodBody_FallsBackToSnapToNearest()
    {
        // Arrange — "_shape = shape;" at line 17 in ShapeCalculator.cs (inside constructor body)
        // No declaration on this line, so line-level returns null → falls back to column 1 snap-to-nearest.
        string filePath = fixture.ServicesFile("ShapeCalculator.cs");

        // Act — column omitted, falls back
        string result = await tools.GoToDefinition(Loc(filePath, 17), ct: TestContext.Current.CancellationToken);

        // Assert — snap-to-nearest from inside constructor body resolves the _shape field
        result.ShouldContain("_shape");
    }

    // ── maxBodyLines ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(3, true)] // small limit triggers truncation
    [InlineData(9999, false)] // large limit means no truncation
    public async Task GoToDefinition_WithMaxBodyLines_ControlsTruncation(int maxBodyLines, bool shouldTruncate)
    {
        // Arrange — Circle.cs line 3: `: Shape` — cursor on Shape (navigates to Shape class)
        string filePath = fixture.ShapesFile("Circle.cs");

        // Act
        string result = await tools.GoToDefinition(Loc(filePath, 3, 42), true, maxBodyLines: maxBodyLines, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Body:");
        if (shouldTruncate)
        {
            result.ShouldContain($"body truncated at {maxBodyLines} lines");
        }
        else
        {
            result.ShouldNotContain("body truncated");
        }
    }
}
