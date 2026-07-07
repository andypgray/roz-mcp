using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Tests.Infrastructure;

/// <summary>
///     Tests for <see cref="SessionContext.Resolve" />'s env-var precedence chain and per-process
///     fallback. These mutate the two real session-id vars, so they live in one dedicated class
///     (xUnit serializes tests within a class) and no other test reads those vars — race-safe.
/// </summary>
public sealed class SessionContextTests
{
    private static readonly string RoslynSessionId = RozEnvVars.SessionId.Name;
    private static readonly string ClaudeSessionId = RozEnvVars.ClaudeSessionId.Name;

    [Fact]
    public void Resolve_RoslynSessionIdSet_WinsOverClaudeId()
    {
        try
        {
            // Arrange
            Environment.SetEnvironmentVariable(RoslynSessionId, "roslyn-wins");
            Environment.SetEnvironmentVariable(ClaudeSessionId, "claude-loses");

            // Act
            string result = SessionContext.Resolve();

            // Assert
            result.ShouldBe("roslyn-wins");
        }
        finally
        {
            ClearSessionVars();
        }
    }

    [Fact]
    public void Resolve_OnlyClaudeSessionIdSet_ReturnsClaudeId()
    {
        try
        {
            // Arrange
            Environment.SetEnvironmentVariable(RoslynSessionId, null);
            Environment.SetEnvironmentVariable(ClaudeSessionId, "claude-abc123");

            // Act
            string result = SessionContext.Resolve();

            // Assert
            result.ShouldBe("claude-abc123");
        }
        finally
        {
            ClearSessionVars();
        }
    }

    [Fact]
    public void Resolve_NeitherSet_ReturnsEightCharFallback()
    {
        try
        {
            // Arrange
            ClearSessionVars();

            // Act
            string result = SessionContext.Resolve();

            // Assert
            result.ShouldNotBeNullOrWhiteSpace();
            result.Length.ShouldBe(8);
        }
        finally
        {
            ClearSessionVars();
        }
    }

    [Fact]
    public void Resolve_RoslynSessionIdWhitespace_FallsThroughToClaudeId()
    {
        try
        {
            // Arrange — RawString treats whitespace as unset, so the chain falls through.
            Environment.SetEnvironmentVariable(RoslynSessionId, "   ");
            Environment.SetEnvironmentVariable(ClaudeSessionId, "claude-fallthrough");

            // Act
            string result = SessionContext.Resolve();

            // Assert
            result.ShouldBe("claude-fallthrough");
        }
        finally
        {
            ClearSessionVars();
        }
    }

    [Fact]
    public void Current_IsNonEmpty_AndStableAcrossReads()
    {
        // Act — Current is the cached production accessor the file logger consumes; it is
        // resolved once on first access against the ambient process environment.
        string first = SessionContext.Current;
        string second = SessionContext.Current;

        // Assert — always a usable id regardless of which source won, and stable (resolved once).
        first.ShouldNotBeNullOrWhiteSpace();
        second.ShouldBe(first);
    }

    private static void ClearSessionVars()
    {
        Environment.SetEnvironmentVariable(RoslynSessionId, null);
        Environment.SetEnvironmentVariable(ClaudeSessionId, null);
    }
}
