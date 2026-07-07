using Zphil.Roz.Symbols;

namespace Zphil.Roz.Tests.Symbols;

public class EditSymbolResolverTests
{
    [Fact]
    public void NoSymbolAtPositionException_WithDescription_IncludesDescription()
    {
        // Act
        InvalidOperationException ex = EditSymbolResolver.NoSymbolAtPositionException(
            "Foo.cs", 10, 5, "on keyword 'public'");

        // Assert
        ex.Message.ShouldContain("Foo.cs:10:5");
        ex.Message.ShouldContain("on keyword 'public'");
        ex.Message.ShouldNotContain("unknown");
    }

    [Fact]
    public void NoSymbolAtPositionException_WithNullDescription_ShowsUnknown()
    {
        // Act
        InvalidOperationException ex = EditSymbolResolver.NoSymbolAtPositionException(
            "Bar.cs", 3, 1, null);

        // Assert
        ex.Message.ShouldContain("Bar.cs:3:1");
        ex.Message.ShouldContain("unknown");
    }
}
