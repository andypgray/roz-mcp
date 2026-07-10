namespace Zphil.Roz;

/// <summary>
///     Raised by the batch write path (<see cref="Utility.AtomicFileWriter.WriteBatchAtomicAsync" />) when
///     a file's on-disk content no longer matches what the edit was computed against — an external process
///     (another editor, <c>dotnet format</c>, a branch switch) wrote the file during the batch's
///     compute window. Writing would silently discard that external change, so the batch aborts before
///     touching anything and reports the conflicted paths.
/// </summary>
/// <remarks>
///     A dedicated <see cref="UserErrorException" /> subtype (surfaced to the client without being logged
///     as a crash) so callers and tests can distinguish a conservative write-conflict abort — the user
///     re-runs the tool against current file contents — from an unexpected write fault. This is the
///     lost-update guard for the verified-write commit paths; the per-op <c>verify=None</c> single-file
///     path (<see cref="Utility.AtomicFileWriter.WriteAtomicAsync" />) does not carry it (accepted
///     millisecond window).
/// </remarks>
internal sealed class FileConflictException : UserErrorException
{
    public FileConflictException(string message) : base(message) { }
}
