using Microsoft.CodeAnalysis;
using Zphil.Roz.Extensions;

namespace Zphil.Roz.Models;

/// <summary>
///     A lightweight fingerprint of diagnostics captured at a point in time.
///     Used to diff against the current state and report only new diagnostics.
/// </summary>
internal sealed class DiagnosticBaseline
{
    private readonly HashSet<DiagnosticKey> keys;
    private readonly IReadOnlyDictionary<DiagnosticKey, DiagnosticSeverity> severities;

    private DiagnosticBaseline(HashSet<DiagnosticKey> keys, IReadOnlyDictionary<DiagnosticKey, DiagnosticSeverity> severities)
    {
        this.keys = keys;
        this.severities = severities;
        CapturedAtUtc = DateTime.UtcNow;
    }

    public DateTime CapturedAtUtc { get; }

    public int Count => keys.Count;

    public IReadOnlySet<DiagnosticKey> Keys => keys;

    public bool Contains(DiagnosticKey key) => keys.Contains(key);

    /// <summary>
    ///     Returns the baseline keys whose captured severity is at or above <paramref name="minSeverity" />.
    /// </summary>
    /// <remarks>
    ///     The baseline captures every non-Hidden diagnostic, but an incremental query filters by its
    ///     own severity floor; without this a below-floor baseline key is absent from the live result
    ///     and would be mis-counted as "resolved."
    /// </remarks>
    public IReadOnlyCollection<DiagnosticKey> KeysAtOrAboveSeverity(DiagnosticSeverity minSeverity) =>
        severities.Where(kv => kv.Value >= minSeverity).Select(kv => kv.Key).ToList();

    /// <summary>
    ///     Creates a baseline from pre-collected diagnostics.
    /// </summary>
    public static DiagnosticBaseline CaptureFrom(List<Diagnostic> diagnostics, string solutionDir)
    {
        HashSet<DiagnosticKey> keySet = new();
        Dictionary<DiagnosticKey, DiagnosticSeverity> severityMap = new();
        foreach (Diagnostic d in diagnostics)
        {
            if (d.Severity == DiagnosticSeverity.Hidden)
            {
                continue;
            }

            if (!d.Location.IsInSource || d.Location.IsInGeneratedFile())
            {
                continue;
            }

            var key = DiagnosticKey.From(d, solutionDir);
            keySet.Add(key);
            severityMap[key] = d.Severity;
        }

        return new DiagnosticBaseline(keySet, severityMap);
    }
}

/// <summary>
///     Identity key for a diagnostic that survives line-number shifts after edits.
///     Uses (Id, RelPath, Message) — deliberately excludes line numbers.
/// </summary>
internal readonly record struct DiagnosticKey(string Id, string RelPath, string Message)
{
    public static DiagnosticKey From(Diagnostic diagnostic, string solutionDir)
    {
        string relPath = diagnostic.Location.IsInSource
            ? Path.GetRelativePath(solutionDir, diagnostic.Location.GetLineSpan().Path)
            : "(metadata)";

        return new DiagnosticKey(diagnostic.Id, relPath, diagnostic.GetMessage());
    }
}
