using Zphil.Roz.Resources;
using Zphil.Roz.Services;

namespace Zphil.Roz.Tests.Services;

/// <summary>
///     Unit tests for <see cref="CodeFixService.ResolveEquivalenceKey" /> — the conservative-writes gate
///     that decides which fix flavor apply_code_fix applies. Pure logic (no workspace), so the ambiguity
///     rules are pinned directly: a single flavor is auto-selected, a requested key is validated, and
///     multiple flavors error rather than silently picking one.
/// </summary>
public class ApplyCodeFixEquivalenceKeyTests
{
    private const string Id = "IDE0044";

    [Fact]
    public void ResolveEquivalenceKey_SingleKey_ReturnsIt()
    {
        // Arrange
        List<(string Title, string? Key)> actions = [("Add readonly modifier", "key_readonly")];

        // Act
        string? key = CodeFixService.ResolveEquivalenceKey(actions, Id, null);

        // Assert
        key.ShouldBe("key_readonly");
    }

    [Fact]
    public void ResolveEquivalenceKey_SingleNullKeyAction_ReturnsNull()
    {
        // Arrange — one action, no equivalence key: FixAll on a null key is unambiguous here.
        List<(string Title, string? Key)> actions = [("Fix it", null)];

        // Act
        string? key = CodeFixService.ResolveEquivalenceKey(actions, Id, null);

        // Assert
        key.ShouldBeNull();
    }

    [Fact]
    public void ResolveEquivalenceKey_MultipleDistinctKeys_NoRequest_ThrowsListingFlavors()
    {
        // Arrange
        List<(string Title, string? Key)> actions =
            [("Add readonly modifier", "key_readonly"), ("Suppress with #pragma", "key_suppress")];

        // Act / Assert — never a silent pick; the error lists both flavors so the caller can choose.
        UserErrorException ex = Should.Throw<UserErrorException>(() => CodeFixService.ResolveEquivalenceKey(actions, Id, null));
        ex.Message.ShouldContain("multiple fixes");
        ex.Message.ShouldContain("key_readonly");
        ex.Message.ShouldContain("key_suppress");
        ex.Message.ShouldContain(RozResources.EditingGuideUri);
    }

    [Fact]
    public void ResolveEquivalenceKey_RequestedValidKey_ReturnsIt()
    {
        // Arrange
        List<(string Title, string? Key)> actions =
            [("Add readonly modifier", "key_readonly"), ("Suppress with #pragma", "key_suppress")];

        // Act
        string? key = CodeFixService.ResolveEquivalenceKey(actions, Id, "key_suppress");

        // Assert
        key.ShouldBe("key_suppress");
    }

    [Fact]
    public void ResolveEquivalenceKey_RequestedUnknownKey_ThrowsListingFlavors()
    {
        // Arrange
        List<(string Title, string? Key)> actions =
            [("Add readonly modifier", "key_readonly"), ("Suppress with #pragma", "key_suppress")];

        // Act / Assert
        UserErrorException ex = Should.Throw<UserErrorException>(() => CodeFixService.ResolveEquivalenceKey(actions, Id, "key_bogus"));
        ex.Message.ShouldContain("key_bogus");
        ex.Message.ShouldContain("not offered");
    }

    [Fact]
    public void ResolveEquivalenceKey_MultipleNullKeyActions_Throws()
    {
        // Arrange — two flavors that both lack an equivalence key can't be disambiguated by FixAll.
        List<(string Title, string? Key)> actions = [("Fix one way", null), ("Fix another way", null)];

        // Act / Assert
        UserErrorException ex = Should.Throw<UserErrorException>(() => CodeFixService.ResolveEquivalenceKey(actions, Id, null));
        ex.Message.ShouldContain("cannot disambiguate");
        ex.Message.ShouldContain(RozResources.EditingGuideUri);
    }

    [Fact]
    public void ResolveEquivalenceKey_NoActions_Throws()
    {
        // Act / Assert
        UserErrorException ex = Should.Throw<UserErrorException>(() => CodeFixService.ResolveEquivalenceKey([], Id, null));
        ex.Message.ShouldContain("registered no fixes");
    }
}
