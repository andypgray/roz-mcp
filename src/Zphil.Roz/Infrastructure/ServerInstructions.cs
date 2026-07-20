namespace Zphil.Roz.Infrastructure;

/// <summary>
///     Loads the embedded server-instructions.md resource for use as MCP ServerInstructions.
/// </summary>
internal static class ServerInstructions
{
    internal static readonly string Text = EmbeddedResourceText.Load("Zphil.Roz.server-instructions.md");
}
