namespace Zphil.Roz.Prompts;

/// <summary>
///     Shared text fragments for the multi-step Roslyn workflow prompts (<c>cleanup_dead_code</c>,
///     <c>assess_impact</c>, <c>tighten_accessibility</c>, <c>fix_diagnostics</c>,
///     <c>check_breaking_changes</c>, <c>triage_coverage</c>, <c>triage_complexity</c>,
///     <c>trim_dependencies</c>, <c>assess_upgrade</c>). Centralizes the blind-spot warnings and the public-API /
///     baseline / verify / change-kind / tool-preflight boilerplate so every prompt stays consistent and
///     the hard-won lessons (the Razor blind spot above all) live in exactly one place.
/// </summary>
internal static class PromptFragments
{
    /// <summary>
    ///     One-sentence statement of the workspace's biggest blind spot: it doesn't index Razor/markup,
    ///     so reference-based tools under-report usage there. Embed in an opening warning — each prompt
    ///     adds its own "so do X" action (keep it / fold it in / don't narrow it).
    /// </summary>
    internal const string RazorBlindSpot =
        "the Roslyn workspace does NOT index Razor/Blazor markup or inline `@code` (nor `.cshtml` "
        + "or other generated/markup sources), so reference-based tools UNDER-report there";

    /// <summary>
    ///     Instruction to snapshot the diagnostics baseline before mutating, so the verify step can
    ///     isolate new breakage from pre-existing (often Razor-generator) noise.
    /// </summary>
    internal const string BaselineCapture =
        "Capture a diagnostics baseline now — `get_diagnostics` with `resetBaseline=true` — so the "
        + "verify step can tell new breakage from pre-existing (often Razor-generator) errors.";

    /// <summary>
    ///     How to enumerate a <em>namespace</em> scope, shared by <c>cleanup_dead_code</c> and
    ///     <c>tighten_accessibility</c> so the guidance can't drift between them. No tool enumerates a
    ///     namespace directly — <c>project=</c> matches a <em>project</em> name, not a namespace — so this
    ///     routes a namespace to the directory that mirrors it (a <c>filePaths</c> glob), with a
    ///     text-search fallback for the layouts where directories and namespaces diverge. Reads as the
    ///     "a namespace → …" arm of a scope-resolution list, so embed it after the file/project/type arms.
    /// </summary>
    internal const string NamespaceScopeHint =
        "a namespace → `get_symbols_overview` with `filePaths=[\"<dir>/**/*.cs\"]` for the directory that "
        + "mirrors it (`project=` matches a project name, not a namespace, and no tool enumerates a "
        + "namespace directly); if the directory layout doesn't mirror the namespaces, text-search for "
        + "`namespace <X>` declarations and pass those files instead — mind the `maxFiles` cap and batch "
        + "if the namespace spans more files than one call allows";

    /// <summary>
    ///     The free-text-change → <c>analyze_change_impact</c> <c>changeKind</c> mapping table. Its sole
    ///     consumer is <c>assess_impact</c> (forward: map one <em>proposed</em> change to a <c>changeKind</c>
    ///     and run it against the current code). <c>check_breaking_changes</c> deliberately does NOT replay
    ///     already-made changes through <c>analyze_change_impact</c> — the tool models a proposed change
    ///     against current code, so on an edit that's already landed three of the four kinds misreport (a
    ///     removed symbol no longer resolves, a retyped one self-compares to all-<c>Compatible</c>, an
    ///     already-narrowed one fails the strictly-narrower validation) — so it censuses consumers with
    ///     <c>find_references</c> and classifies by hand instead. Authored for interpolation at a one-level
    ///     (3-space) sub-bullet indent: the first bullet inherits the interpolation hole's indentation; the
    ///     continuation bullets carry it inline — keep the hole at that indent in the caller so the list
    ///     stays aligned.
    /// </summary>
    internal const string ChangeKindMapping =
        "- a new type for a property/field/return/parameter → `TypeChange` + `newType=<type>`\n"
        + "   - deleting the symbol → `RemoveSymbol`\n"
        + "   - reducing visibility → `AccessibilityNarrow` + `newAccessibility=<level>`\n"
        + "   - any other signature edit (add/remove/reorder/retype a parameter) → `SignatureChange` + `newSignature=<param-list>` (e.g. `(string name, int count = 5)`) for precise per-argument verdicts";

    /// <summary>
    ///     Overflow / headless fallbacks appended to every <see cref="AsMultipleChoice" /> rendering.
    ///     A parenthetical (not a sentence) because the fragment interpolates mid-sentence at ~12 ask
    ///     sites: it covers the case where there are more options than the client's picker can show, and
    ///     the case where the recipe runs non-interactively with no user to answer.
    /// </summary>
    private const string ChoiceFallbacks =
        " (split the options across several consecutive questions if there are more than the UI can "
        + "offer; if running non-interactively with no user to answer, take the most conservative option "
        + "and say so)";

    /// <summary>
    ///     Recipe phrasing that turns a decision point into a multiple-choice question. Behavior-first
    ///     and client-agnostic: it never names a client tool, so Claude Code renders it as its native
    ///     multiple-choice UI while other clients degrade to an inline question. <paramref name="multiSelect" />
    ///     selects the tick-all-that-apply wording over the pick-exactly-one wording. The caller supplies the
    ///     options inline (they're runtime-derived, so they can't live here). Both branches carry the
    ///     <see cref="ChoiceFallbacks" /> overflow/headless tail.
    /// </summary>
    internal static string AsMultipleChoice(bool multiSelect) =>
        multiSelect
            ? "present it as a multiple-choice selection where I can tick every option that applies "
              + "(each option a short label plus a one-line description), and wait for my picks" + ChoiceFallbacks
            : "present it as a multiple-choice selection where I pick exactly one option (each a short "
              + "label plus a one-line description), and wait for my pick" + ChoiceFallbacks;

    /// <summary>
    ///     The public-API confirmation gate, branched on <paramref name="handling" />
    ///     (<c>ask</c> / <c>exclude</c> / <c>include</c>). Shared by <c>cleanup_dead_code</c> and
    ///     <c>tighten_accessibility</c>; <paramref name="actionVerb" /> (e.g. "delete"/"narrow") and
    ///     <paramref name="actionGerund" /> (e.g. "deleting"/"narrowing") tailor the wording.
    /// </summary>
    internal static string GetPublicApiGate(string? handling, string actionVerb, string actionGerund)
    {
        return handling?.Trim().ToLowerInvariant() switch
        {
            "exclude" =>
                "**Public API gate.** This scope is a library whose public surface is consumed outside "
                + "the solution. Treat every externally-visible member (`public`/`protected` on an "
                + $"accessible type) as off-limits — do NOT {actionVerb} it. Confine {actionGerund} to "
                + "`internal`, `private`, and `file`-scoped symbols.",
            "include" =>
                "**Public API gate.** I've confirmed this scope has no external consumers, so treat "
                + $"`public`/`protected` members like any other — {actionVerb} them on the same evidence "
                + "as internal/private members. (Still apply the reachability and markup checks above.)",
            _ =>
                $"**Public API gate — STOP and ask me before {actionGerund} any externally-visible "
                + "member.** `find_references` only sees callers inside this solution, so a "
                + "`public`/`protected` member may still be intentional public API consumed elsewhere. "
                + $"List the flagged externally-visible members and ask which are safe to {actionVerb} — "
                + $"{AsMultipleChoice(true)}; treat unticked as off-limits. **Do not "
                + $"block the rest of the work on that answer:** {actionVerb} the `internal`/`private`/"
                + "`file`-scoped symbols now — they don't need confirmation — and hold only the "
                + "externally-visible ones pending my reply."
        };
    }

    /// <summary>
    ///     The post-change verification paragraph. <paramref name="whatBroke" /> names the change
    ///     (e.g. "your deletion") and <paramref name="onBreak" /> is the recovery instruction shown
    ///     when something no longer compiles. <paramref name="baselineRef" /> names where the baseline
    ///     was captured (e.g. "the step-1 baseline", "the baseline you just captured") so the verify text
    ///     never points at a baseline the recipe didn't take: a recipe that captures its baseline
    ///     mid-flow — or hasn't captured one at all — would otherwise inherit the step-1 phrasing and tell
    ///     the agent to diff against a baseline that isn't there.
    /// </summary>
    internal static string GetVerifyStep(string whatBroke, string onBreak, string baselineRef) =>
        "`get_diagnostics` shares the Razor blind spot and reports pre-existing generator errors that "
        + $"aren't yours — so use `incremental=true` (against {baselineRef}) to see only what "
        + $"{whatBroke} broke, and/or run `dotnet build` as the authoritative check. {onBreak}";

    /// <summary>
    ///     Opening preflight that makes a recipe robust to tool-scoping. The server may be launched with a
    ///     restricted <c>ROZ_TOOLS</c> subset (or a client that loads tools on demand), so a tool a
    ///     recipe calls might not be registered — without this, the agent only discovers that mid-recipe
    ///     and thrashes (as happened to <c>trim_dependencies</c> when <c>get_unused_references</c> wasn't in
    ///     the default preset). Names the recovery — use the per-step fallback, or enable the tool via
    ///     <c>ROZ_TOOLS</c> and reconnect — so a missing tool surfaces up front and actionably. Pass
    ///     <paramref name="notInDefault" /> to call out a core tool the recipe depends on that is NOT in the
    ///     <c>default</c> preset (e.g. <c>get_unused_references</c>), giving the exact enable command.
    /// </summary>
    internal static string ToolPreflight(string? notInDefault = null)
    {
        string specific = String.IsNullOrWhiteSpace(notInDefault)
            ? ""
            : $"This recipe's core tool `{notInDefault}` is NOT in the `default` preset, so if it isn't "
              + $"registered, enabling it means `ROZ_TOOLS=default,{notInDefault}` (then reconnect the server). ";

        return
            "**Preflight — tools.** This recipe drives the roz-mcp server's tools, and a server launched "
            + "with a restricted `ROZ_TOOLS` subset may not register every one it needs. " + specific
            + "If a tool a step calls comes back unknown/unavailable, don't thrash — name the missing tool "
            + "and either use the fallback that step gives, or, if it has none, tell me to enable it (add it "
            + "to `ROZ_TOOLS` and reconnect the server, or run `roz-mcp setup --tools=…`) and pause "
            + "until I do.";
    }
}
