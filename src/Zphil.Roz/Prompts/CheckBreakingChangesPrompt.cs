using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Zphil.Roz.Prompts;

/// <summary>
///     MCP prompt that reports the breaking-change surface of work already done on this branch — diffs the
///     <c>public</c>/<c>protected</c> declarations changed since a baseline git ref, censuses each one's
///     in-solution consumers with a single batched <c>find_references</c>, and classifies source / binary /
///     behavioral breaks by hand. It deliberately does NOT route through <c>analyze_change_impact</c>: that
///     tool models a <em>proposed</em> change against <em>current</em> code, so replaying an already-made
///     edit through it misreports (a removed symbol no longer resolves, a retyped one self-compares to
///     all-<c>Compatible</c>, an already-narrowed one fails the strictly-narrower validation).
///     Backward-looking ("what did I already break for consumers") — the mirror of <c>assess_impact</c>'s
///     forward "what breaks if I change this." Read-only.
/// </summary>
[McpServerPromptType]
internal sealed class CheckBreakingChangesPrompt
{
    /// <summary>
    ///     Emits a read-only recipe that diffs the public surface against <paramref name="baseline" />,
    ///     censuses each change's in-solution consumers with <c>find_references</c>, and classifies the
    ///     breaks by hand, optionally scoped by <paramref name="scope" />.
    /// </summary>
    [McpServerPrompt(Name = "check_breaking_changes", Title = "Check for breaking API changes")]
    [Description(
        "Report what your branch's changes would break for consumers of your public API, versus a "
        + "baseline git ref — source, binary, and behavioral breaks. Read-only.")]
    public static string CheckBreakingChanges(
        [Description("Git ref to diff against (the 'before' state) — default 'main'. Use the branch or tag your consumers build against.")]
        string baseline = "main",
        [Description("Optional project-name substring to scope the analysis to one project; omit for the whole solution.")]
        string? scope = null)
    {
        string scopeClause = String.IsNullOrWhiteSpace(scope)
            ? ""
            : $" Scope every `find_references` call to `project={scope}`.";

        return
            $"""
             Report what already-made changes on this branch would break for consumers of this code, relative
             to `{baseline}` — read-only, after the fact. This is the backward-looking mirror of
             `assess_impact`: it inspects edits you've already committed, not a hypothetical one. Make no edits.

             {PromptFragments.ToolPreflight()}

             1. **Find the changed public surface.** Run `git diff {baseline}...HEAD -- '*.cs'` (merge-base
                form — the changes on this branch since it forked from `{baseline}`; if `{baseline}` doesn't
                resolve as a git ref, fall back to the repo's default branch — `git symbolic-ref --short
                refs/remotes/origin/HEAD`, e.g. `origin/master`). From the hunks, list the
                **`public` and `protected`** declarations that were added, removed, retyped, had their
                signature changed, or had their visibility narrowed. Also list any `public`/`protected`
                member whose **body** changed with no change to its declaration line — a same-signature,
                different-behavior edit is its own break class, and it's the only input to step 4's *behavior
                change* bucket. Ignore `internal`/`private`/`file` changes — they can't break external
                consumers (except via `InternalsVisibleTo`; flag that if the changed project grants it).

             2. **Census the in-solution consumers with `find_references` — NOT `analyze_change_impact`.**
                **Do NOT run `analyze_change_impact` here.** It models a *proposed* change against the
                *current* code, so on an edit you've already made it misreports: a **removed** symbol no longer
                resolves, a **retyped** one self-compares to all-`Compatible`, and an **already-narrowed** one
                fails the tool's strictly-narrower validation. Census by hand instead:
                - **Surviving declarations** (retyped / re-signatured / narrowed / body-changed — anything
                  still present at HEAD): ONE batched `find_references` with `referenceKinds=all`,
                  `includeTests=true`, and `symbolNames=[...]` over all of them at once.{scopeClause}
                - **Removed declarations** (gone at HEAD, so there's no symbol to resolve): enumerate their
                  consumers from the git-diff hunks and text-search the solution for each removed name — a
                  lingering hit is either a now-broken consumer or an unrelated same-name survivor, so check
                  which.
                **Honesty about the blind spot:** `find_references` only sees references *inside this solution*
                — consumers in other repos or published packages are invisible. So split the report:
                **in-solution impact** (the census above) versus the **public-surface delta** (what an external
                caller would face — the declaration changed, but their call sites can't be seen from here).

             3. **Razor cross-check.** {PromptFragments.RazorBlindSpot}. Text-search the markup
                (`*.razor`, `*.cshtml`) for each changed symbol name and fold any hits in as extra,
                unclassified consumer sites.

             4. **Classify and report — no edits.** Per changed declaration, name every break a consumer would
                hit — one change can land in more than one bucket, so say which:
                - **Source-incompatible** (their code won't recompile): removed or renamed; visibility
                  narrowed; a parameter added / removed / reordered / retyped with no implicit conversion; a
                  return type changed.
                - **Binary-incompatible** (already-compiled callers break even if source still compiles): any
                  signature or return-type change — *including adding an optional parameter or a `params`
                  array*, which is source-compatible but NOT binary-compatible — plus field→property and a new
                  member added to an interface.
                - **Behavior change** (same signature, different semantics): the body-only edits from step 1.
                Attach each declaration's step-2 consumer sites, group **in-solution** (censused) separately
                from **external-surface** (unverifiable here), and lead with the headline count of breaking
                versus safe changes. This is a report — make no edits.

             Start with step 1.
             """;
    }
}
