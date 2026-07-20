# Editing C# with roz

roz has five mutating tools — `edit_symbol`, `rename_symbol`, `replace_content`, `apply_code_fix`, and
`change_signature`. They are built around one design rule: **read-only actions are forgiving, writes are
extremely conservative.** A write resolves against the live document, refuses rather than guesses when a
change isn't provably safe, and can report the compiler impact of the edit in the same round trip. This
guide covers the parts that aren't obvious from the tool schemas.

## Verified writes (`verify=None|Delta|DryRun`)

All five mutating tools accept a `verify` mode that folds the edit → build → re-read loop into one call:

- **`None`** (default) — apply and write, no build, no delta. The original behaviour.
- **`Delta`** — apply, **commit**, then report new and resolved **compiler errors** across the changed
  projects and everything that transitively depends on them. It commits *before* verifying: a break is
  reported, never auto-reverted. Report, don't police.
- **`DryRun`** — apply to an in-memory copy of the solution, report the same delta, and **write nothing**
  (a rename defers the physical file move to a note).

Caveats that apply to every verified write:

- **Compiler-only.** The delta diffs compiler errors, not analyzer diagnostics — an analyzer regression
  won't show up.
- **Razor blind spot.** The workspace doesn't index `.razor` / `.cshtml` / `@code`, so deltas never see
  breakage there. Cross-check markup by hand or with a real build.
- **Atomic at end of batch.** A batch commits at the very end; a call cancelled mid-batch writes nothing.
- **Out-of-band conflict abort.** Before writing, the batch re-checks each target's on-disk content
  against what the edit was computed from. If a file changed underneath the edit, the whole batch aborts
  with a conflict error and nothing is written.
- **Non-workspace targets.** A `replace_content` path outside every loaded project is written on `Delta`,
  skipped on `DryRun`, and flagged as having no delta coverage.

`DryRun` is the safe preview: run it first when you're unsure whether an edit compiles, read the delta,
then re-run with `Delta` (or `None`) to land it.

## `change_signature` and its apply gate

`change_signature` applies the **deterministic** subset of a signature change — add-parameter-with-default,
remove-unused-parameter, and reorder-with-named-arguments — across a method's whole override/interface
family **and** all of its call sites in one call. `newSignature` is a parameter-list descriptor, e.g.
`(string name, int count = 5)`.

It is analysis-gated. If **any** site can't be changed safely and mechanically, it **refuses and writes
nothing**, returning each blocker as a `file:line — reason` line. Blocker classes include:

- A reorder that would silently rebind a positional call to different parameters.
- A removed parameter still used in a family body, or a dropped argument with a side effect.
- An un-rewritable site — attribute usage, a reduced extension-method receiver, `params` expansion.
- Any reference in a generated file.

Some changes are rejected up front as **analysis-only in v1**: retype, `ref`/`out` changes, `params`-shape
changes, and adding a required parameter (no default). Preview those with
`analyze_change_impact changeKind=SignatureChange newSignature=…` and edit by hand.

**Recommended flow:** run `analyze_change_impact changeKind=SignatureChange newSignature=…` first to see
every site classified Compatible / RequiresUpdate / Unsafe, then run `change_signature` to apply it when
the analysis is clean. The analyzer reports the blast radius; `change_signature` applies it when every
site is safe.

## `apply_code_fix` and equivalence keys

`apply_code_fix` runs one diagnostic ID's registered Roslyn fixer via FixAll across a scope — the bulk-fix
path that `get_diagnostics`'s "Available analyzer fixes" hint points at. The fixers come from the **target
solution's** analyzer packages (xUnit.analyzers, StyleCop, NetAnalyzers, Roslynator, …); roz ships none of
its own.

- **Equivalence keys.** When a fixer offers several flavours for one diagnostic, `apply_code_fix` refuses
  rather than silently pick one — the error lists each flavour as `Title (key=…)`. Pass `equivalenceKey`
  to choose (e.g. "add readonly" vs "suppress with pragma"). A single-flavour fixer is auto-selected.
- **`includeTests`** defaults to `false`, symmetric with `get_diagnostics`. The most commonly available
  fixers (xUnit) only fire in test projects, so pass `includeTests=true` to *both* tools when cleaning
  test code — otherwise a test-only diagnostic reads as "nothing to fix".
- **No match is not an error.** If the scope has no diagnostics for the ID, the result is an informative
  `SkippedReason`, not a failure. (`ROZ_DISABLE_ANALYZERS` legitimately yields empty for analyzer IDs.)
- **Scope.** `project` / `filePaths` genuinely narrow which diagnostics get fixed; omit them for the
  whole solution.

## Special symbol names

Some members have internal Roslyn names that differ from their source syntax. Use these names with
`edit_symbol` (any action) and name-based lookup:

| Source syntax | Symbol name | Notes |
|---|---|---|
| `ClassName(...)` | `.ctor` | Instance constructor (includes an implicit default constructor) |
| `static ClassName()` | `.cctor` | Static constructor |
| `~ClassName()` | `Finalize` | Destructor / finalizer |
| `operator +` | `op_Addition` | User-defined operators (`op_Subtraction`, `op_Multiply`, …) |
| `implicit operator T` | `op_Implicit` | Implicit conversion |
| `explicit operator T` | `op_Explicit` | Explicit conversion |
| `this[int i]` | `this[]` | Indexer |

Two traps:

- **Passing the class name targets the class declaration**, not its constructor. To edit a constructor use
  the symbol name `.ctor` (or `.cctor`). `edit_symbol` guards the specific case of replacing a type
  declaration with a constructor body and tells you to use `.ctor`.
- **`rename_symbol` rejects** constructors, destructors, operators, and indexers — rename the containing
  class instead.

## Insertion indentation

For member insertions (`edit_symbol action=Insert` of a method/property/field), Roslyn's formatter runs
automatically and a blank-line separator is ensured between the inserted member and its neighbour — you
don't hand-indent. For **trivia** insertions (comments, or attributes passed as raw text), the formatter
doesn't reflow them, so the caller is responsible for indentation.
