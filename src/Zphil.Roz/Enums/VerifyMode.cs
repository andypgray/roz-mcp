namespace Zphil.Roz.Enums;

/// <summary>
///     Post-edit verification mode for the mutating tools (<c>edit_symbol</c>, <c>replace_content</c>,
///     <c>rename_symbol</c>). A single enum rather than two booleans: half the schema cost and no
///     nonsensical combinations.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             <see cref="None" /> — today's behavior, a byte-identical code path. No delta is computed.
///         </item>
///         <item>
///             <see cref="Delta" /> — commit the edit, then report the new/resolved <em>compiler errors</em>
///             across the changed projects and everything that transitively depends on them. Commits
///             <em>before</em> verifying, so a cancelled or faulting verification can never hold the edit
///             hostage (report, don't police — no auto-revert); a verification fault after the commit is
///             surfaced with a message stating the edit landed. The batch stages in memory and commits
///             atomically at the end, so a call cancelled mid-batch writes nothing — under
///             <see cref="None" />, each completed op is already on disk.
///         </item>
///         <item>
///             <see cref="DryRun" /> — apply the batch to an in-memory <see cref="Microsoft.CodeAnalysis.Solution" />
///             fork, report the same delta plus per-op outcomes, and write nothing to disk.
///         </item>
///     </list>
/// </remarks>
internal enum VerifyMode
{
    None,
    Delta,
    DryRun
}
