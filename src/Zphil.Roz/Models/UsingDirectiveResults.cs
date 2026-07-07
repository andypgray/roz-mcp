namespace Zphil.Roz.Models;

internal sealed record AddUsingsResult(
    List<string> Added,
    List<string> AlreadyPresent,
    List<string> AlreadyGloballyImported,
    string RelPath);

internal sealed record FileUsingsResult(
    string RelPath,
    List<string> Removed,
    List<string> Skipped,
    string? Error = null);

internal sealed record RemoveUnusedUsingsResult(
    List<FileUsingsResult> Files);
