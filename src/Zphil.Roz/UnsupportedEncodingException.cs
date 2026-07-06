namespace Zphil.Roz;

/// <summary>
///     Raised when an edit or reconcile read encounters a file that is not UTF-8 (a non-UTF-8 BOM,
///     or no-BOM bytes that fail strict UTF-8 decoding — typically a legacy codepage such as
///     Windows-1252). Decoding such a file as UTF-8 would silently corrupt high bytes into
///     U+FFFD / mojibake and write the corruption back, so the edit tools reject instead.
/// </summary>
/// <remarks>
///     A dedicated <see cref="UserErrorException" /> subtype so that
///     <see cref="Infrastructure.WorkspaceManager" />'s reconcile sweep can catch <em>only</em>
///     encoding failures (and skip the offending file) without masking other user errors that
///     would signal a genuine reconcile fault. Being a <see cref="UserErrorException" />, it is
///     surfaced to the MCP client as a friendly error by the request pipeline without being logged
///     as a crash.
/// </remarks>
internal sealed class UnsupportedEncodingException : UserErrorException
{
    public UnsupportedEncodingException(string message) : base(message) { }
}
