using Zphil.Roz.Prompts;

namespace Zphil.Roz.Tests.Prompts;

/// <summary>
///     Branch tests for the <see cref="PromptFragments.GetPublicApiGate" /> handling arms. The
///     prompt snapshot tests render every prompt with default arguments, which exercises only the
///     ask arm — the <c>exclude</c>/<c>include</c> arms fire when the user passes a
///     <c>publicApi</c> argument, so they are pinned here directly.
/// </summary>
public class PromptFragmentsTests
{
    [Theory]
    [InlineData("exclude")]
    [InlineData(" Exclude ")]
    public void GetPublicApiGate_Exclude_ForbidsTouchingExternallyVisibleMembers(string handling)
    {
        // Act — case-insensitive and trimmed, so client argument forms all land on the same arm.
        string gate = PromptFragments.GetPublicApiGate(handling, "delete", "deleting");

        // Assert
        gate.ShouldContain("off-limits");
        gate.ShouldContain("do NOT delete");
        gate.ShouldContain("Confine deleting");
    }

    [Fact]
    public void GetPublicApiGate_Include_TreatsPublicMembersLikeAnyOther()
    {
        // Act
        string gate = PromptFragments.GetPublicApiGate("include", "narrow", "narrowing");

        // Assert
        gate.ShouldContain("no external consumers");
        gate.ShouldContain("narrow them on the same evidence");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ask")]
    [InlineData("unrecognized")]
    public void GetPublicApiGate_DefaultOrUnknown_FallsBackToAskArm(string? handling)
    {
        // Act
        string gate = PromptFragments.GetPublicApiGate(handling, "delete", "deleting");

        // Assert
        gate.ShouldContain("STOP and ask me");
    }
}
