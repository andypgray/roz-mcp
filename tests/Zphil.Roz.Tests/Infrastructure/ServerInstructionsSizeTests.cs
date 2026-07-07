using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Tests.Infrastructure;

public class ServerInstructionsSizeTests
{
    /// <summary>
    ///     Claude Code truncates MCP server instructions at 2 KB.
    ///     Guard against accidentally exceeding this limit.
    /// </summary>
    [Fact]
    public void ServerInstructions_FitWithinClaudeCodeLimit()
    {
        const int maxChars = 2048;

        ServerInstructions.Text.Length.ShouldBeLessThanOrEqualTo(maxChars,
            $"server-instructions.md is {ServerInstructions.Text.Length} chars — exceeds the {maxChars}-char Claude Code limit. Trim it.");
    }
}
