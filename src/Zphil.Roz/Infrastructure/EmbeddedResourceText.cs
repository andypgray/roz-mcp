using System.Reflection;

namespace Zphil.Roz.Infrastructure;

/// <summary>
///     Reads a UTF-8 text resource embedded in this assembly by its manifest (logical) name. Shared by
///     the consumers that embed a markdown file and read it to a string at load time — the server
///     instructions (<see cref="ServerInstructions" />), the setup snippet
///     (<see cref="ProjectInstructionsSnippet" />), and the MCP guide resources
///     (<see cref="Resources.RozResources" />) — so a renamed file or drifted resource id fails loudly
///     and identically in all of them.
/// </summary>
internal static class EmbeddedResourceText
{
    /// <summary>
    ///     Loads the embedded resource named <paramref name="logicalName" /> and returns its full text.
    /// </summary>
    /// <param name="logicalName">
    ///     The manifest resource id — the assembly-qualified logical name, for example
    ///     <c>Zphil.Roz.server-instructions.md</c>.
    /// </param>
    /// <exception cref="InvalidOperationException">No resource with that id is embedded in the assembly.</exception>
    internal static string Load(string logicalName)
    {
        Assembly assembly = typeof(EmbeddedResourceText).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(logicalName)
                              ?? throw new InvalidOperationException(
                                  $"Embedded resource '{logicalName}' not found.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
