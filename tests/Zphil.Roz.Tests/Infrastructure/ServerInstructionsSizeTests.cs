using System.Text;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Resources;

namespace Zphil.Roz.Tests.Infrastructure;

public class ServerInstructionsSizeTests
{
    /// <summary>
    ///     Claude Code truncates MCP server instructions at 2 KB, measured in UTF-8 bytes — the true
    ///     wire constraint. Multi-byte glyphs in the instructions (`—`/`→`/`⇒` are 3 bytes each) make the
    ///     byte count the one that matters, so pin bytes, not chars.
    /// </summary>
    [Fact]
    public void ServerInstructions_FitWithinClaudeCodeLimit()
    {
        const int maxBytes = 2048;
        int byteCount = Encoding.UTF8.GetByteCount(ServerInstructions.Text);

        byteCount.ShouldBeLessThanOrEqualTo(maxBytes,
            $"server-instructions.md is {byteCount} UTF-8 bytes — exceeds the {maxBytes}-byte Claude Code limit. Trim it.");
    }

    /// <summary>
    ///     The always-loaded instructions must signpost both on-demand guide resources by URI, so an
    ///     agent knows the detailed configuration and editing guidance exists and how to reach it.
    /// </summary>
    [Fact]
    public void ServerInstructions_SignpostsGuideResources()
    {
        ServerInstructions.Text.ShouldContain(RozResources.ConfigurationGuideUri);
        ServerInstructions.Text.ShouldContain(RozResources.EditingGuideUri);
    }
}
