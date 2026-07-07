namespace Zphil.Roz.Utility;

internal static class TextUtility
{
    internal static int CountLines(string text) => text.AsSpan().Count('\n') + 1;
}
