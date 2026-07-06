namespace Zphil.Roz.Enums;

/// <summary>
///     The kind of proposed change whose blast radius <c>analyze_change_impact</c> reports.
/// </summary>
/// <remarks>
///     All kinds produce precise per-site verdicts. <see cref="SignatureChange" /> is coarse only when
///     invoked <em>without</em> a <c>newSignature</c> descriptor (every call site flagged
///     <c>RequiresUpdate</c>); supplying <c>newSignature</c> switches on per-argument
///     Compatible/RequiresUpdate/Unsafe classification via real overload resolution in a forked solution.
/// </remarks>
internal enum ChangeKind
{
    SignatureChange,
    RemoveSymbol,
    TypeChange,
    AccessibilityNarrow
}
