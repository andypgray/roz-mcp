using System.Reflection;

namespace Zphil.Roz.Infrastructure;

/// <summary>
///     Loads the embedded server-instructions.md resource for use as MCP ServerInstructions.
/// </summary>
internal static class ServerInstructions
{
    internal static readonly string Text = LoadEmbeddedResource();

    private static string LoadEmbeddedResource()
    {
        Assembly assembly = typeof(ServerInstructions).Assembly;
        using Stream stream = assembly.GetManifestResourceStream("Zphil.Roz.server-instructions.md")
                              ?? throw new InvalidOperationException("Embedded resource 'Zphil.Roz.server-instructions.md' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
