using Zphil.Roz.Presentation;

namespace Zphil.Roz.Tests.Presentation;

public class BatchGuardsTests
{
    [Fact]
    public void EnforceBatchOrPositions_BothNull_Throws()
    {
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            BatchGuards.EnforceBatchOrPositions(null, null));

        ex.Message.ShouldContain("Provide one of");
        ex.Message.ShouldContain("locations");
        ex.Message.ShouldContain("symbolNames");
    }

    [Fact]
    public void EnforceBatchOrPositions_BothNonNull_Throws()
    {
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            BatchGuards.EnforceBatchOrPositions(["Foo"], ["src/Foo.cs:1:1"]));

        ex.Message.ShouldContain("Pass either locations");
        ex.Message.ShouldContain("symbolNames");
    }

    [Fact]
    public void EnforceBatchOrPositions_EmptySymbolNames_Throws()
    {
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            BatchGuards.EnforceBatchOrPositions([], null));

        ex.Message.ShouldContain("symbolNames must not be empty");
    }

    [Fact]
    public void EnforceBatchOrPositions_EmptyLocations_Throws()
    {
        UserErrorException ex = Should.Throw<UserErrorException>(() =>
            BatchGuards.EnforceBatchOrPositions(null, []));

        ex.Message.ShouldContain("locations must not be empty");
    }

    [Fact]
    public void EnforceBatchOrPositions_OnlySymbolNames_DoesNotThrow() => Should.NotThrow(() => BatchGuards.EnforceBatchOrPositions(["Foo"], null));

    [Fact]
    public void EnforceBatchOrPositions_OnlyLocations_DoesNotThrow() => Should.NotThrow(() => BatchGuards.EnforceBatchOrPositions(null, ["src/Foo.cs:1:1"]));
}
