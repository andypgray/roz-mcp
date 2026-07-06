namespace Zphil.Roz;

/// <summary>
///     An expected, user-facing validation error (bad input, ambiguous symbol, file not found, etc.).
///     Caught by <see cref="Pipeline.GlobalCallToolFilter" /> and returned to the MCP client
///     without polluting the file log, which is reserved for unexpected crashes.
/// </summary>
/// <remarks>
///     Not sealed: <see cref="UnsupportedEncodingException" /> derives from it so that
///     <see cref="Infrastructure.WorkspaceManager" /> can catch encoding failures specifically on
///     the reconcile path without swallowing every <see cref="UserErrorException" />.
/// </remarks>
internal class UserErrorException : InvalidOperationException
{
    public UserErrorException(string message) : base(message) { }

    public UserErrorException(string message, Exception innerException) : base(message, innerException) { }
}
