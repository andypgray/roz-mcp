# roz-mcp architecture

`roz-mcp` is a .NET global tool (`net10.0`) that runs as a
[Model Context Protocol](https://modelcontextprotocol.io) server over stdio. It exposes
Roslyn-powered C# semantic navigation and editing (find references, go to definition, type
hierarchies, impact analysis, and a small set of conservative refactors) to MCP clients such as
Claude Code, Cursor, VS Code Copilot Chat, and Codex CLI.

This document is the design deep-dive for contributors and technically curious users. It explains
why the server is shaped the way it is; the README covers what each tool does. For build, test, and
contribution mechanics see `CONTRIBUTING.md`.

## Table of contents

1. [Overview](#overview)
2. [Repository layout](#repository-layout)
3. [Design philosophy: forgiving reads, conservative writes](#design-philosophy-forgiving-reads-conservative-writes)
4. [Request pipeline and error handling](#request-pipeline-and-error-handling)
5. [The tool surface](#the-tool-surface)
6. [Deep dives](#deep-dives)
7. [Prompts](#prompts)
8. [Workspace and build internals](#workspace-and-build-internals)
9. [Diagnostics and analyzers](#diagnostics-and-analyzers)
10. [Configuration reference](#configuration-reference)
11. [Operational notes](#operational-notes)
12. [Dependencies](#dependencies)

## Overview

The server is a single process. `Program.cs` uses `System.CommandLine` to parse CLI arguments:

- The `setup` subcommand runs a per-project onboarding flow (see
  [Setup and onboarding](#setup-and-onboarding)).
- The root command starts the MCP server when stdin is piped (the normal case: an MCP host
  launches the tool and speaks the protocol over stdio) and prints a usage hint when invoked
  interactively at a terminal.
- `--help` and `--version` are provided automatically by `System.CommandLine`.

Before any Roslyn workspace type loads, `MsBuildBootstrap.Initialize()` runs. This ordering is
load-bearing: the MSBuild instance must be selected and registered before the first
`MSBuildWorkspace` type is JIT-resolved. The two entry points that touch the workspace
(`StartMcpServerAsync` and `RunServerAsync`) therefore carry a `NoInlining` barrier, which keeps
the bootstrap from being hoisted ahead of them. See [MSBuild bootstrap](#msbuild-bootstrap).

Once running, every request flows through the same short pipeline:

```
MCP tool method → Service (returns a typed result record) → ResponseFormatter → plain-text response
```

All tool methods return `string` (formatted text). The server deliberately does not use MCP
structured content or output schemas; it renders human-readable text and lets the model read it.
See [Request pipeline and error handling](#request-pipeline-and-error-handling).

## Repository layout

| Directory | Contents |
|---|---|
| `src/Zphil.Roz/Tools/` | MCP `[McpServerToolType]` classes, one per category (`NavigationTools`, `ReferenceTools`, `TypeHierarchyTools`, `CodeEditTools`, `DiagnosticTools`, `WorkspaceTools`, `UsingDirectiveTools`). |
| `src/Zphil.Roz/Services/` | Business logic between the tools and Roslyn. Each service takes the `WorkspaceManager` and returns typed result records. |
| `src/Zphil.Roz/Services/DiRecognizers/` | Per-container dependency-injection registration recognizers (see [DI container detection](#di-container-detection)). |
| `src/Zphil.Roz/Models/` | Typed result records, one file per category (`NavigationResults.cs`, `ReferenceResults.cs`, `CodeEditResults.cs`, `DiagnosticResults.cs`, …). |
| `src/Zphil.Roz/Presentation/` | `ResponseFormatter` (result record → text) and `SymbolFormatter` (per-symbol rendering). |
| `src/Zphil.Roz/Pipeline/` | MCP filters (`GlobalCallToolFilter`, `EditSerializationFilter`), `ToolSelector`, `ResponseTruncator`, and the input coercers. |
| `src/Zphil.Roz/Infrastructure/` | `WorkspaceManager`, the environment-variable registry (`RozEnvVars`), MSBuild bootstrap, the orphaned-server watchdogs, and Serilog configuration. |
| `src/Zphil.Roz/Symbols/` | Location parsing (`LocationParser`), fully-qualified-name parsing, and special-member resolution. |
| `src/Zphil.Roz/Prompts/` | `[McpServerPromptType]` classes, the user-invoked workflow recipes (see [Prompts](#prompts)). |
| `src/Zphil.Roz/Setup/` | The `roz-mcp setup` onboarding flow and its per-client writers. |
| `src/Zphil.Roz/Resources/` | `RozResources` and the embedded markdown guides served as `roz://guides/*` MCP resources (see [On-demand guide resources](#on-demand-guide-resources)). |

The pipeline stays thin: tools format, services compute, models hold the shape of a result. A tool
method contains no Roslyn logic of its own: it calls one service, hands the record to
`ResponseFormatter`, and returns the string.

## Design philosophy: forgiving reads, conservative writes

The design principle everything else follows from:

> Read-only operations are forgiving. Write operations are extremely conservative.

A read tool that resolves the wrong symbol wastes a round trip; a write tool that resolves the wrong
symbol corrupts source. The two halves of the tool surface are therefore held to opposite standards.

### Read-only resolution is snap-to-nearest

Navigation and reference tools (`find_references`, `go_to_definition`, `find_implementations`,
`get_type_hierarchy`, …) resolve a cursor forgivingly:

- A cursor that lands on the `public` keyword (or any other keyword) walks up to the enclosing
  declaration. The exception is comments and doc comments, which are rejected outright: a cursor
  there carries no symbol, and guessing would be worse than erroring.
- **Line-level resolution**: when a location is `"path:line"` (no column), the tool resolves the
  member declared on that line (for example `GetLargest`, not the `IShape` in its return type).
  With `"path:line:col"` the exact position is used.
- **Special case**: `go_to_definition` on the `override` keyword navigates to the overridden *base*
  member (one level up), not the override's own declaration.

The resolver therefore leans toward producing *an* answer. When a position genuinely resolves to no
symbol, the error names what was
found instead ("in a comment", "on keyword 'public'") via `RoslynExtensions.ClassifyPosition`.

### Write resolution is strict, and `edit_symbol` is name-first

Mutating tools invert the bias; their contracts are asymmetric with the read tools:

- **`edit_symbol`** treats the symbol name, scoped to the file plus an optional
  `containingType`/`kind`, as authoritative, resolved against the live (drained) document. A unique
  in-file match is edited and any `:line:col` cursor is ignored. This is what keeps a same-file
  batch correct: once an earlier edit shifts every later edit's line numbers, a line-based cursor
  would be stale, but a name-based match still lands. A `:line:col` cursor is used only to
  tie-break same-name overloads (matched against the identifier-token span, not the declaration
  span); a stale or unresolvable cursor among overloads raises an actionable ambiguity error. A bare
  `path:line` paired with a `symbolName` is normalized to path-only.
- **`rename_symbol`** is strict. It accepts path-only or a full `path:line:col` (a bare `path:line`
  is rejected), the cursor must sit on the identifier token, and the `symbolName` is cross-checked
  against the symbol Roslyn resolves at that position. There is no name-first fallback. A rename has
  a solution-wide blast radius (it rewrites every reference, renames files to match, and repairs
  `#if` branches), so the position↔name interlock is worth the strictness. The worst case it exists
  to prevent is an unintended solution-wide rename.
- **`replace_content`** defaults to literal search, which supports multi-line blocks, the preferred
  mode for exact block replacement. Regex is opt-in (`isRegex=true`) and uses .NET multiline
  semantics (`^`/`$` match line boundaries); single-line mode (`.` matches newlines) is a further
  opt-in on top of regex.
- **Member insertions** get Roslyn's formatter applied automatically, with a blank-line separator
  ensured between the inserted member and its neighbour. For trivia insertions (comments, attributes
  supplied as text) the caller owns the indentation.

### Forgiving input coercion

MCP clients (especially models) routinely send parameters in shapes that are *almost* right. The
schemas advertise the strict types, but a set of coercer factories (registered via
`ToolInputSerializerOptions`) silently rescues the common malformed shapes that would otherwise die
as a byte-position deserializer error. This is a forgiving-input *policy*, not a contract change:
the advertised schema is unchanged, and genuinely wrong shapes still throw a friendly
`UserErrorException` that names the offending token.

- **`StringArrayCoercerFactory`** accepts a stringified array (`"[\"A\",\"B\"]"` → `["A","B"]`) and a
  bare string (`"A"` → `["A"]`). Numbers, objects, and other tokens still throw.
- **`StringCoercerFactory`** is the symmetric counterpart for scalar `string` slots: it unwraps a
  single-element array (`["A"]` → `"A"`) and treats an empty array (`[]`) as `null`. Strings that
  merely *look* like arrays (`"[A]"`) pass through verbatim, because `replace_content` search
  patterns must survive untouched.
- **`EnumArrayCoercerFactory`** is the enum analog: `memberKinds=["Method","Property"]`, a
  stringified equivalent, and a bare `"Method"` all bind. Each element is validated with
  `Enum.IsDefined`; unknown names throw the same valid-values error as the scalar enum validator.
  Integer elements are not admitted as enum values.
- **`BatchRequestArgumentNormalizer`** wraps a bare `edits: { … }` object into `edits: [{ … }]` for
  `edit_symbol` and `replace_content` (the object analog of the `"A"` → `["A"]` rescue). It runs as
  a request filter (via `GlobalCallToolFilter`), *not* as a `JsonConverterFactory`: registering a
  custom converter on the record-array type would force the schema exporter to emit `{}` for
  `edits`. That would strip the rich nested-record schema (field descriptions, defaults, implicit
  enum lists) that clients rely on. The other coercers can reconstruct their trimmed schema via
  `SchemaTrimmer` injection because their shapes are trivial; the nested record schema is not, so
  the normalizer runs as a filter instead.

## Request pipeline and error handling

Exceptions thrown anywhere in a tool call are caught by `GlobalCallToolFilter` (registered in
`Program.cs`) and returned to the client as `IsError = true`. What distinguishes an *expected* error
from a *bug* is the exception type, and the server treats that distinction as a hard design rule.

### `UserErrorException` vs. crashes

`UserErrorException` is the type for expected, user-facing errors: bad input, ambiguous
symbols, a file that does not exist, no matching symbol, a validation failure. `GlobalCallToolFilter`
surfaces these to the client without logging, because they are not bugs; they are the normal
result of the tool being asked something it cannot answer.

Any other exception type (a plain `InvalidOperationException`, a `NullReferenceException`, an
unexpected Roslyn failure) is treated as a **crash**. The filter logs it at Warning and *then*
surfaces it. This split is what keeps the log signal meaningful: everything in it is a real bug,
because expected errors never reach it.

The following invariants hold across the pipeline:

- Services and resolvers throw `UserErrorException` for anything the user can correct. Anything
  else propagating out is, by definition, a crash worth logging.
- Read-only batch fan-out (`BatchOrSingle.RunAllAsync`) catches only `UserErrorException`. A
  per-name user error becomes an inline `=== Error: {name} ===` block so the rest of the batch still
  returns; an unexpected exception still faults the batch so it gets logged.
- Catch-all handlers (`catch (Exception)`) are deliberately absent from the request pipeline. A
  catch-all would swallow crashes into the user-error path and destroy the signal the whole design
  exists to preserve.
- One approved exception to the no-catch-all rule: the write-batch fan-out in `SymbolEditService`
  catches unexpected exceptions *per operation*, because one bad edit must not fault an entire
  multi-edit batch. It logs at Warning before wrapping the failure into an `EditSymbolErrorOp`, so
  the crash signal is preserved. User errors are caught separately with no log, symmetric with the
  read-only fan-out.

## The tool surface

The server registers 19 tools, grouped into seven category keys. Read-only tools run concurrently;
mutating tools are serialized (see [Concurrency and serialization](#concurrency-and-serialization)).

| Category key | Tools |
|---|---|
| `workspace` | `get_workspace_info`, `get_unused_references` |
| `navigation` | `find_symbol`, `get_symbols_overview`, `go_to_definition`, `find_overloads`, `analyze_method` |
| `references` | `find_references`, `find_implementations`, `analyze_change_impact` |
| `types` | `get_type_hierarchy` |
| `editing` | `edit_symbol`, `rename_symbol`, `replace_content`, `apply_code_fix`, `change_signature` |
| `usings` | `add_usings`, `remove_unused_usings` |
| `diagnostics` | `get_diagnostics` |

Notes on specific tools:

- **`find_references`** accepts `referenceKinds=all|invocations|reads|writes`. `invocations` narrows
  to call sites; `reads`/`writes` split by value flow. It supports `includeOverloads`,
  `excludeBaseCalls`, and receiver-type filtering via `containingType`, and emits an
  interface-dispatch tip when the target is an interface member.
- **`find_implementations`** works at two levels: on an interface member or a virtual/abstract member
  it returns implementations and overrides; on a class or interface *type* it returns derived classes
  or implementing types.
- **`get_unused_references`** finds `<ProjectReference>`/`<PackageReference>` entries unused by source.
  `dependencyKind=Projects` (the default) is a confident signal; `dependencyKind=Packages` is a
  *weak* signal, because analyzers, source generators, and runtime-only dependencies leave no symbol
  in source; `dependencyKind=All` reports both.
- **`get_workspace_info`** surfaces the target framework and project type even for .NET Framework and
  non-SDK projects: `NET48`/`NET472`-style compiler symbols and a `<TargetFrameworkVersion>v4.8</…>`
  csproj fallback both resolve to `net48`, and the `<OutputType>WinExe</OutputType>` plus a
  `System.Windows.Forms` reference (absent `UseWindowsForms`) classifies as WinForms.

### Selective tool loading (`ROZ_TOOLS`)

`ROZ_TOOLS` selects which tools get registered for a session; a smaller set cuts per-session schema
overhead. The value is a comma- or semicolon-delimited list of tokens,
processed left-to-right, each of which may be:

- a **preset**: `all`, `default`, `read`, `navigate`, or `edit`;
- a **category key** from the table above;
- an **individual tool name**;
- any of the above prefixed with `-` to exclude it from the set built so far (e.g.
  `all,-usings,-edit_symbol`).

Unknown tokens are dropped with a stderr warning. If *every* token is invalid, startup throws.
Recognized tokens that net to an empty set (exclusion-only input) start the server with no tools and
log a warning, so it does not look like a silent startup failure. Full resolution rules live in
[`ToolSelector.cs`](src/Zphil.Roz/Pipeline/ToolSelector.cs).

When `ROZ_TOOLS` is unset, the **`default` preset** applies. It registers 12 tools: the
read-only tools minus `get_unused_references`, plus `rename_symbol`. It excludes seven tools:

- the destructive write tools `edit_symbol`, `replace_content`, `apply_code_fix`,
  `change_signature`, `add_usings`, and `remove_unused_usings`;
- and the niche `get_unused_references`.

The excluded set is defined by two arrays in `ToolSelector`: `RiskyToolsExcludedFromDefault` (the
seven destructive/niche tools) and `HeldFromDefaultPendingValidation` — an A/B gate for tools that
are not risky, just unproven: a tool lands there until an evaluation confirms it earns its context
cost. `rename_symbol` is in neither list: it is the one mutating tool the default preset ships,
because in a client that follows the setup routing it is still gated behind a write-confirmation
prompt.

The hold list is currently empty; both tools that passed through it graduated on evidence.
`analyze_change_impact` was promoted to back the `assess_impact` and `tighten_accessibility`
prompts: its user-invoked value superseded the earlier A/B hold. `analyze_method` was held by its
2026-06 A/B (a bare tool addition was not adopted) and promoted on 2026-07-20 after a routed
re-test: with a routing cue in place the arm adopted the tool, cut cost 19% and turns 27%, and
judged method-report recall came out parity-or-better (see the addendum in the evidence doc). The
evaluations that drove these decisions are published under evidence:

- [`docs/evidence/ab-test-analyze-change-impact-2026-06-01.md`](docs/evidence/ab-test-analyze-change-impact-2026-06-01.md)
- [`docs/evidence/ab-test-analyze-method-2026-06-04.md`](docs/evidence/ab-test-analyze-method-2026-06-04.md)

You can seed a project's tool set during onboarding with `roz-mcp setup --tools=<preset>`.

### Location format

Cursor positions are strings of the form `"path"`, `"path:line"`, or `"path:line:col"` (for example
`src/Foo.cs:42:15`). Name-based lookup uses `symbolNames=["A","B","C"]`. Cursor and name modes are
alternatives; they are never combined.

- `find_references`, `find_implementations`, `find_overloads`, `get_type_hierarchy`,
  `analyze_change_impact`, and `analyze_method` take `locations[]` or `symbolNames[]` and batch
  many items per call. One MCP round trip carrying `symbolNames=["A","B",…,"K"]` is far cheaper
  than N parallel calls; a batch is split only when per-item filters (`kind`, `project`,
  `includeTests`) differ. Each cursor needs at least `:line`.
- `go_to_definition` takes a single `location` string; `:col` is optional, and a line-only
  `path:line` snaps to the member declared on that line.
- `edit_symbol` takes a single `location` string, with `symbolName` authoritative as described above.
- `rename_symbol` takes a single `location` string: path-only or full `:line:col` only.
- `add_usings` takes a single `filePath` (a path, no cursor). `replace_content`'s per-edit `filePath`
  is likewise a path only.

MSBuild's diagnostic format (`Foo.cs(42,15)`, as printed by `dotnet build`) is silently accepted and
normalized, which is convenient when lifting a location straight out of a build-error message.

### Parameter naming convention

A single convention governs the parameter slots that recur across tools:

| Slot | Name |
|---|---|
| Scalar location (cursor, may include `:line` or `:line:col`) | `location` |
| Batch locations | `locations[]` |
| File-only scalar (no cursor) | `filePath` |
| File-only batch | `filePaths[]` |
| Symbol-name batch | `symbolNames[]` |
| Singular symbol name | `symbolName` |

Batch-item record properties (`EditSymbolRequest`, `ReplaceContentRequest`) mirror the slots in
PascalCase. Semantic parameters such as `usings`, `diagnosticIds`, `kind`, and `severity` fall
outside the rule and keep their own names. A snapshot test pins the whole parameter surface so drift
is caught in review.

### Fully-qualified names and special symbol names

Every tool that accepts `symbolName` supports dotted fully-qualified names: type only
(`TestFixture.Shapes.Circle`), `Type.Member` (`Circle.GetArea`), full namespace-qualified
(`TestFixture.Shapes.Circle.GetArea`), constructors (`Circle..ctor`), and operators
(`ShapeCollection.op_Addition`). FQN resolution is case-insensitive and searches the whole
solution.

Limitations:

- Concrete generic FQNs are not supported; use a simple name plus `containingType`.
- Open generic syntax *is* supported for arity disambiguation: `Processor<>` (arity 1),
  `Processor<,>` (arity 2), with simple names or FQNs.
- FQN `symbolName` and `containingType` are mutually exclusive; use one or the other.
- When an FQN does not match, resolution falls through to standard name-based search.

Some members carry internal Roslyn names that differ from their source syntax. These names are used
with `edit_symbol` and with name-based lookup. `rename_symbol` rejects constructors, destructors,
operators, and indexers; rename the containing class instead.

| Source syntax | Symbol name | Notes |
|---|---|---|
| `ClassName(...)` | `.ctor` | Instance constructor (includes an implicit default constructor) |
| `static ClassName()` | `.cctor` | Static constructor |
| `~ClassName()` | `Finalize` | Destructor / finalizer |
| `operator +` | `op_Addition` | User-defined operators (`op_Subtraction`, `op_Multiply`, …) |
| `implicit operator T` | `op_Implicit` | Implicit conversion |
| `explicit operator T` | `op_Explicit` | Explicit conversion |
| `this[int i]` | `this[]` | Indexer |

Passing the class name targets the class declaration itself, not its constructor.

Name resolution also reaches metadata. `find_implementations`, `get_type_hierarchy`, and
`go_to_definition` resolve external (BCL/NuGet) types by name when no source match is found; the
Roslyn compilation already has referenced-assembly types loaded. For example,
`find_implementations symbolName=IDisposable` (type dispatch) finds every solution type that
implements `IDisposable`, even though `IDisposable` is not declared in the solution. `find_symbol`
resolves FQNs to metadata symbols (e.g. `System.Collections.Generic.List`) and auto-includes their
XML docs.

### The `project` filter is resolution-scoping, never result-filtering

All read-only tools accept `project`, a case-insensitive substring match against project names. It
narrows *which projects a name-based lookup resolves against*; that disambiguates a cross-project
name clash when no `location` is given. It never post-filters the references, callers, or impact sites a
tool returns. A `find_references` or `analyze_change_impact` blast radius is therefore always the
full solution-wide count: `project` cannot silently undercount fan-in. (The per-`[ProjectName]`
distribution already in the output gives you the scoped view when you want it.)

- **Name-based lookup** (`symbolNames=[…]`): `project` selects the projects the name resolves
  against, and throws (listing every solution project name) when it matches none.
- **Cursor lookup** (`locations=[…]`): a cursor already targets one symbol, so `project` plays no
  resolution role; the reference and impact tools surface it as *ignored* (a one-line note) rather
  than silently dropping cross-project results. A typo'd project name still errors.
- **`get_workspace_info`** is the one exception: there `project` selects which projects are
  *reported* (it is an info listing, not a symbol lookup) and returns empty when it
  matches none.

### Parameter defaults and cross-parameter constraints

Read-only tools default toward source-only results; every widening is opt-in:

- **`includeTests`** defaults `false`: test projects are excluded unless set true (internally
  inverted to `excludeTests`; detection is described under
  [Test-project classification](#test-project-classification)).
- **`includeGenerated`** defaults `false`: generated files (`obj/`, `*.g.cs`, `*.designer.cs`, …)
  are excluded unless set true.
- **`includeMetadata`** (on `find_implementations`) defaults `false`: BCL/NuGet results are excluded
  unless set true.

Some parameter combinations are contradictory, and the server rejects them with a
`UserErrorException`:

- **`includeOverloads` / `excludeBaseCalls`** on `find_references` are invocation-only refinements:
  each promotes `referenceKinds=all` to `invocations`, so pairing either with an explicit
  `referenceKinds=reads|writes` is an error.
- **`incremental` and `resetBaseline`** on `get_diagnostics` cannot be combined, because a reset
  wipes the very baseline `incremental` would diff against.

### Output enrichment: `includeDocs`, `includeBody`, `contextLines`

Several read tools accept optional enrichments:

- **`includeDocs=true`** (seven tools: `find_symbol`, `go_to_definition`, `get_symbols_overview`,
  `find_implementations`, `get_type_hierarchy`, `find_overloads`, `analyze_method`) adds parsed XML
  documentation. `<inheritdoc/>` is resolved by walking override/implementation chains up to ten
  levels; `<see cref>`, `<paramref>`, and `<c>` tags are flattened to plain text. For metadata-only
  symbols, docs are included automatically regardless of the flag.
- **`includeBody=true`** (five tools: `find_symbol`, `go_to_definition`, `find_implementations`,
  `find_overloads`, `analyze_method`) inlines each result's full dedented source. `maxBodyLines` caps
  the output, with a note giving the true length.
- **`contextLines=N`** (`find_references`, `analyze_change_impact`, `analyze_method`) shows N lines of
  source before and after each match, like `grep -C`.

### DI container detection

When a constructor or type has no direct callers, the reference scanner falls back to detecting
dependency-injection registrations, where construction happens via reflection. Explicit
registrations are recognized across eight containers (Microsoft.Extensions.DependencyInjection,
Autofac, Ninject, Unity, Simple Injector, DryIoc, Lamar/StructureMap, and Castle Windsor), one
recognizer per container under `Services/DiRecognizers/`. Output tags the container and lifetime,
e.g. `[Autofac, scoped]`. Nested builder patterns (MassTransit `AddConsumer`, Quartz `AddJob`,
MediatR `AddBehavior`, OpenTelemetry `AddSource`) are also picked up via `FluentChainHelper`.
`find_implementations` always appends DI-registration info when available.

**Not detected**: convention-based or assembly-scanning registrations
(`RegisterAssemblyTypes(...).Where(...)`). The type name never appears in the code, so static
analysis has nothing to find.

## Deep dives

### Verified writes (`verify`)

Editing normally means a round trip: apply the edit, run a build to see what broke, read the result.
The five `editing` tools (`edit_symbol`, `rename_symbol`, `replace_content`, `apply_code_fix`,
`change_signature`) collapse that loop into one call through a `verify` enum
([`VerifyMode.cs`](src/Zphil.Roz/Enums/VerifyMode.cs)):

- **`None`** (default): the original behaviour, a byte-identical code path. No delta is
  computed.
- **`Delta`**: commit the edit, then report the new and resolved compiler errors across the
  changed projects and everything that transitively depends on them (via
  `GetProjectsThatTransitivelyDependOnThisProject`). The edit commits *before* verification runs, so
  the policy is *report, don't police*: a break is reported and the edit stays committed.
- **`DryRun`**: apply the batch to an in-memory `Solution` fork, report the same delta, and write
  nothing to disk (`rename_symbol` defers its physical file move to a note).

**Mechanism.** `edit_symbol` and `replace_content` route reads and writes through a null-tolerant
seam (`EditIo`; a null session *is* the `None` path) and stage each operation's final normalized
string into an `EditSession` fork. The fork applies `WithDocumentText` for every `DocumentId` at
a path, so breakage in a multi-target-framework project stays visible. Operation N resolves against
the fork (via `EditSymbolResolver`'s `solutionOverride`), skipping the mid-batch freshness sync.
`rename_symbol` reuses the fork that `Renamer.RenameSymbolAsync` already produces; `apply_code_fix`
reuses the fork FixAll produces; `change_signature` builds its own fork. All of them persist and
verify through one shared fork/verify path (`EditVerificationService.FinalizeForkAsync` over
`SolutionChangeWriter`), so there is a single commit per call. The delta diffs two immutable
snapshots by a line-free `DiagnosticKey` (Id, relative path, message), restricted to errors in
non-generated source files.

**Caveats.** These define the tool's trust boundary:

- The delta is compiler-only, so analyzer regressions are *not* caught.
- The Razor blind spot applies: deltas do not see `.razor`, `.cshtml`, or `@code`. See
  [The Razor blind spot](#the-razor-blind-spot).
- There is no scope cap: editing a widely-depended-on type recompiles most of the solution; the
  scope line and per-project progress ticks make the cost visible.
- A `replace_content` target outside every loaded project is written under `Delta`, skipped under
  `DryRun`, and flagged "no delta coverage".
- Session batches commit atomically at the end. A call cancelled mid-batch writes nothing (under
  `None`, each completed op is already on disk). A verification fault *after* a successful commit is
  surfaced as an error that states the edit landed, so the change is not applied twice.

### `apply_code_fix`

`apply_code_fix` (in the `editing` category, outside the `default` preset) applies a registered
Roslyn `CodeFixProvider` via FixAll for a single `diagnosticId` across a scope. It is the
bulk-fix path the "Available analyzer fixes" hint in `get_diagnostics` points at: when diagnostics
report "40 × IDE0052, fix available", one call replaces 40 hand edits and the model authors nothing.

The fixers come from the target solution's own analyzer packages (`Project.AnalyzerReferences`:
xUnit.analyzers, StyleCop, .NET analyzers, Roslynator, and so on), discovered by
[`FixerCatalog`](src/Zphil.Roz/Services/FixerCatalog.cs); the server ships none of its own. The
mechanism resolves the provider and collects the ID's diagnostics from the same `Solution` snapshot
the fork derives from, so spans align. It serves them to a `FixAllContext` scoped to the solution
via a private precollected diagnostic provider, takes the single resulting changed solution, and
persists and verifies it through the shared fork/verify path.

Its design rules:

- **Scope**: here `project`/`filePaths` genuinely narrow *which diagnostics get fixed*
  (result-affecting), unlike the resolution-scoping `project` on read tools. Omitting scope means the
  whole solution. The precollected diagnostic set is what enforces the scope, so out-of-scope
  projects yield nothing.
- **`includeTests`** defaults `false`, matching `get_diagnostics`. The most-available fixers
  (xUnit's) fire only in test projects, so cleaning test code means passing `includeTests=true` to
  *both* tools. Without it a test-only diagnostic reads as "nothing to fix".
- **Equivalence-key gate**: a fixer offering several flavours produces an error listing every
  `Title` and key. It will never, for instance, choose "suppress with pragma" over "add readonly" on
  your behalf. Pass `equivalenceKey` to choose; a single flavour is auto-selected.
- **Rejections** (all `UserErrorException`): an unknown ID with no registered fixer; a
  fixer with no FixAll support (there is no per-site fallback in v1); a fixer that makes
  non-text/multi-step changes (adding or renaming a file); and a throwing third-party fixer (its
  steps are wrapped in a narrow guard, a scoped exception to the no-catch-all rule).
- **No-match handling**: a no-match produces an informative skip reason ("no '{id}' diagnostics in
  scope").

### `change_signature`

`change_signature` (also `editing`, outside the `default` preset) applies the deterministic subset
of a signature change (add-parameter-with-default, remove-unused-parameter,
reorder-with-named-arguments) across a method's whole override/interface slot family and all of
its call sites, in one round trip. It is the *apply* half of
`analyze_change_impact changeKind=SignatureChange newSignature=…`: the analyzer reports the blast
radius; `change_signature` applies it when every site is safe. The `newSignature` argument is a
parameter-list descriptor (`(string name, int count = 5)`), parsed by `SignatureParser`.

The mechanism runs in stages. It resolves the target with the edit-tool contract (`symbolName` plus
`containingType`/`kind` authoritative, a cursor only as an overload tie-breaker), computes the
signature delta, and rejects non-deterministic changes up front. It then resolves the slot family
and builds a declaration fork: every family declaration's parameter list is rewritten across every
`DocumentId`, and the `<param>` XML doc is synced. Every reference site is classified with the
shared impact analyzer, each `RequiresUpdate` site is re-planned into a concrete argument-list
rewrite, and the result persists through the same shared fork/verify path as `rename_symbol` and
`apply_code_fix`.

Its conservatism is enforced by an **apply gate**: a `UserErrorException` listing every blocker as
`file:line — reason`, with zero writes, unless every site is either `Compatible` or a
mechanically-rewritable `RequiresUpdate`. Blockers include any `Unsafe` site (a reorder trap, a
silent retarget to another overload, a method-group break), a removed parameter still used in a
family body, a side-effectful dropped argument, an un-rewritable site (attribute usage, a
reduced-extension-method receiver, params expansion), or any reference in a generated file.

Two further constraints:

- The apply-gate census is stricter than the read-only reporting path: it runs with tests and
  generated files included. A test call site must be rewritten or the build breaks; a generated-file
  reference is a hard blocker (rewriting generated output is wrong; leaving it stale breaks the
  build).
- v1 is analysis-only for the harder cases: retype, `ref`/`out` kind changes, `params`-shape
  changes, and adding a *required* (no-default) parameter are rejected up front. Preview those with
  `analyze_change_impact newSignature=…` and edit by hand. The caveats of the other verified writes
  apply here unchanged. Because `newSignature` describes exactly one
  method, `change_signature` is single-target; there is no batch form.

### `analyze_change_impact`

`analyze_change_impact` (in the `references` category, and *in* the default preset) resolves a
symbol, takes a proposed `changeKind` plus a target descriptor, and returns a structured
blast-radius report: every reference site tagged
`Compatible` / `RequiresUpdate` / `Unsafe`, a per-severity summary, and a per-project distribution.
It answers "what breaks if I change X" in one round trip instead of a `find_references` followed by
reading each call site by hand. The classifier reuses the read/write/invocation walk in
`ReferenceKindClassifier` and Roslyn's real conversion and accessibility rules.

Four `changeKind`s:

- **`TypeChange`** (requires `newType`, e.g. `newType=long`) classifies each site by value-flow
  direction. A **producer** (a method return or a property/field read) flows into a consumer context
  `C`, and `Compilation.ClassifyConversion(newType, C)` decides the verdict: identity or implicit →
  `Compatible`, explicit → `RequiresUpdate` with a cast hint, none → `Unsafe`. A **consumer** (a
  property or field write) checks the supplied value against `newType`. A **parameter** target routes
  to the containing method's call sites and conversion-checks the argument. `newType` is bound by
  speculative binding at the declaration, so keywords, aliases, and generics resolve in the
  declaration's `using` scope.
- **`RemoveSymbol`** (no target) marks every reference `Unsafe` and adds a note about any
  overrides/implementations that would be orphaned.
- **`AccessibilityNarrow`** (requires `newAccessibility`, which must be *strictly narrower* than the
  current level) applies the C# scope rule for the new level against each site's enclosing type and
  assembly. Access either survives (`Compatible`) or breaks (`Unsafe`); there is no
  `RequiresUpdate`.
- **`SignatureChange`** (the default) is coarse without a `newSignature`: every call site is
  `RequiresUpdate` and a note points at the parameter. Supplying `newSignature` switches on
  precise per-site classification: it resolves the target's full override/interface slot family,
  builds a forked `Solution` in which every family declaration carries the new parameter list, and
  re-binds each reference in the fork. A site that re-binds unchanged is `Compatible`; one a
  deterministic rewrite can fix is `RequiresUpdate` (the rewritten call text is shown); a silent
  reorder-trap rebind, a retarget to another overload, or a no-conversion failure is `Unsafe`.
  Precise mode requires exactly one method target and rejects a slot that extends into metadata (for
  example `object.ToString`), so it forbids batching. `newType`, `newAccessibility`, and
  `newSignature` are each strictly gated to their matching `changeKind`: a mismatched pair errors.

**Precision limits** (the same blind-spot class as the rest of the server): this is static
one-hop data-flow analysis. Out of scope: multi-hop `var` propagation (a local that adopts
the new type and then flows onward is flagged with a ripple note, not traced), reflection, `dynamic`
dispatch, and source-generator output.

## Prompts

Beyond tools, the server registers 10 prompts: user-invoked slash-command recipes that package a
multi-step Roslyn workflow into a single message. Unlike tools, which the model calls, a prompt is
invoked by the user and returns a single user-role message that the agent then executes. They are
registered from every `[McpServerPromptType]` class in `Prompts/`, and their shared boilerplate (the
public-API gate, the Razor blind-spot cross-check, baseline/verify steps, the free-text→`changeKind`
mapping, and the tool-availability preflight) lives in `Prompts/PromptFragments.cs`.

| Prompt | Recipe |
|---|---|
| `cleanup_dead_code` | Iteratively find and remove dead symbols in a scope, using semantic references (not text search), DI/dispatch/markup blind-spot checks, a public-API gate, test-only-reachable pair detection, and a loop-until-stable. |
| `assess_impact` | Resolve a symbol, map a free-text change to an `analyze_change_impact` `changeKind`, and report the blast radius. Report-first; applies only on confirmation. |
| `tighten_accessibility` | Iteratively narrow over-broad accessibility (public → internal, then type-private), verifying each step safe via `analyze_change_impact`, with a public-API gate and an `InternalsVisibleTo` suggestion for members pinned public only by a test assembly. |
| `decompile_symbol` | Resolve an external (BCL/NuGet) symbol, prefer package source over decompilation, fall back to `ilspycmd`, and explain the body. Read-only. |
| `fix_diagnostics` | Baseline, triage, then apply minimal root-cause fixes to compiler and analyzer diagnostics, verifying incrementally and handling both the Razor phantom-error blind spot and the analyzer-fix hints. |
| `check_breaking_changes` | Diff changed `public`/`protected` declarations against a baseline git ref, census each one's in-solution consumers with a single batched `find_references`, and classify source/binary/behavioural breaks by hand. Does *not* use `analyze_change_impact`: it models an already-made change, which replaying would misreport. Read-only. |
| `triage_coverage` | Run coverage, map uncovered line ranges back to the symbols that own them, and triage each gap as dead code, a genuine untested branch, or a low-confidence reflection/markup case. Writes tests only on confirmation. |
| `triage_complexity` | Rank the worst complexity/quality-debt hotspots from an existing metric (or in-server size/coupling proxies when none is available) and route each to the appropriate follow-up prompt. Read-first. |
| `trim_dependencies` | Drive `get_unused_references`, cross-check the weak package signal, remove dead references via the CLI, and verify with a build. Mutating, confirmation-gated. |
| `assess_upgrade` | Gauge a NuGet upgrade's blast radius via `find_references`, map the release's breaking changes onto in-solution sites, report a risk verdict, then upgrade and verify only on confirmation. |

Every prompt opens with a **tool-availability preflight** (`PromptFragments.ToolPreflight`, pinned by
a snapshot test). Because a server started with a restricted `ROZ_TOOLS` subset may not have
registered a tool a recipe calls, the preflight tells the agent to surface the missing tool and its
enable path up front instead of thrashing mid-recipe. `trim_dependencies` is the one prompt whose
core tool (`get_unused_references`) is not in the `default` preset, so its preflight names the exact
enable command (`ROZ_TOOLS=default,get_unused_references`). The alternative, promoting the tool into
`default`, was rejected to keep the default tool surface small.

### The Razor blind spot

The Roslyn workspace does not index `.razor`, `.cshtml`, or `@code` blocks. As a result,
`find_references` can produce false negatives against markup, and `get_diagnostics` can produce
phantom errors for symbols that markup genuinely uses. This is an honest limitation, not a bug, and
the server mitigates rather than hides it: every reference- or diagnostics-based prompt (all but
`decompile_symbol`) bakes in a markup text-search cross-check via
`PromptFragments.RazorBlindSpot`. When
in doubt, trust a real `dotnet build` over the workspace's view of markup.

### On-demand guide resources

The server also registers three MCP resources (`roz://guides/configuration`, `roz://guides/editing`, and `roz://guides/workflows`) via `WithResourcesFromAssembly()`, immediately after the prompts. Each is an `internal static` method on `RozResources` that returns an embedded markdown guide (`Resources/*.md`) as its body. No URI carries a `{param}`, so all three are advertised as direct resources in `resources/list`, and the model (or the user, by `@`-mention) can read them on demand.

The split keeps the always-loaded surfaces small. `server-instructions.md` stays within its byte budget by signposting the configuration and editing URIs instead of carrying their content. The project-instructions snippet that setup writes does the same for the workflows guide, pointing at it instead of carrying a routing table and prompts catalog inline. The guides hold the detail an agent needs only occasionally: every environment variable and the `ROZ_TOOLS` grammar (configuration); the `verify` modes, `change_signature` apply gate, `apply_code_fix` equivalence keys, and special symbol names (editing); the question → tool routing map and the packaged workflow prompts (workflows). Four write-path error messages point at the relevant guide by URI, so a refusal names where its rules live.

## Workspace and build internals

### `WorkspaceManager`

`WorkspaceManager` is a singleton managing the `MSBuildWorkspace` lifecycle. Solution loading starts
eagerly in the constructor as a `Task`. `GetSolutionAsync` / `GetSolutionIfLoaded` gate on a
`solutionReadyTask` signal that fires the moment the snapshot is *usable* (opened, unresolved
references stripped, timestamps recorded), before compilation warmup. Gating before warmup means the
first tool call no longer blocks behind every project compiling. Warmup continues in the background
under an internal `gate`, and the specific compilations a tool touches are forced on demand (and
shared with warmup, since Roslyn memoizes `GetCompilationAsync` per snapshot). Warmup takes a
cancellation token (a per-load CTS linked to a long-lived dispose CTS), so disposal unwinds it
promptly.

The solution itself is located by `FileUtility.DiscoverSolution()`, which finds a `.sln`, `.slnx`,
or `.slnf` by checking, in order: the `ROZ_SOLUTION_PATH` environment variable; a single solution
file in the current working directory; a walk up the parent directories.

### Concurrency and serialization

Read-only tools run concurrently against immutable `Solution` snapshots: there is no shared mutable
state to contend for. Mutating tools are serialized at the MCP filter layer by
`Pipeline/EditSerializationFilter`, and within a single edit an internal `gate` semaphore serializes
the workspace mutations (`NotifyFileChangedAsync`, `ScheduleReloadAsync`). A read never observes a
half-applied write.

### External-edit detection

Files change under the server, from IDE edits, other tools' writes, and branch switches. Two
layers keep the workspace in sync:

- `WorkspaceManager.ReconcileAllExternalEditsAsync` runs at every tool-call entry and sweeps file
  modification times to catch modifications and deletions.
- A background `FileSystemWatcher` opportunistically schedules per-file reloads; file *additions* are
  detected only by this layer.

Both are disabled by `ROZ_DISABLE_AUTO_REFRESH=true`.

### MSBuild bootstrap

`MsBuildBootstrap` selects an MSBuild instance via `vswhere.exe`, preferring stable major versions
(16/17) over preview (18+), and propagates the choice to Roslyn's BuildHost subprocess through
`VSINSTALLDIR` and `VSCMD_VER=99.0` environment variables. Without this, the BuildHost picks the
highest-version Visual Studio install, which (for users who also have a VS preview installed) can
crash on legacy projects that still declare the 2003-era MSBuild XML namespace, via an
`XMakeElements` type-initializer error. Setting `ROZ_VS_INSTALL_PATH` to a VS root overrides the
auto-selection.

The project must also exclude MSBuild runtime assemblies from the package. The csproj declares
`ExcludeAssets="runtime" PrivateAssets="all"` on `Microsoft.Build`, `Microsoft.Build.Framework`,
`Microsoft.Build.Tasks.Core`, `Microsoft.Build.Utilities.Core`, and `Microsoft.NET.StringTools`.
`MSBuildLocator` requires that these are *not* shipped with the app: they are loaded at runtime by
`MSBuildLocator.RegisterDefaults()`. Shipping them triggers the MSBL001 build error.

### Setup and onboarding

`roz-mcp setup [--client=<list>] [--tools=<value>] [--plugin|--no-plugin]` configures a project to
use the server. Four MCP clients are supported, each via its own writer under `Setup/Clients/`:

- **Claude Code**: `.mcp.json`, `.claude/settings.local.json`, and a project-instructions snippet.
- **Cursor**: `.cursor/mcp.json` and an `AGENTS.md` snippet.
- **VS Code Copilot Chat**: `.vscode/mcp.json` (using the `servers` key with `"type":"stdio"`) and
  an `AGENTS.md` snippet.
- **Codex CLI**: `.codex/config.toml` and an `AGENTS.md` snippet.

A zero-argument invocation auto-detects the client from `.claude/`, `.cursor/`, `.vscode/`, or
`.codex/` marker directories; multiple markers prompt for an explicit `--client=`. The JSON and TOML
writers share idempotent merge logic that preserves sibling MCP servers and any user-added `env`
entries. A generic project-instructions snippet is written to each client's rules file: created when
the file is missing, appended when the `# roz-mcp` section is absent, and replaced in place when the
section is already there, so a re-run picks up snippet changes from newer releases. Hand edits inside
the section do not survive a re-run; content outside it is preserved byte-exactly.

For Claude Code specifically, the settings file auto-allows the read-only tools and routes the
mutating tools to the client's permission-prompt list, so that writes prompt for confirmation. This
aligns the client's permission layer with the server's conservative-writes design. (The snippet the
setup flow writes lives in the user's own project rules file, such as their `CLAUDE.md` or
`AGENTS.md`, which is a user-owned target distinct from this server's source.) The other clients use
their own per-tool approval models, so no allow/ask list is emitted for them.

Claude Code also has a plugin path that replaces the server-registration half of `setup`. The
repository doubles as a single-plugin marketplace (`.claude-plugin/marketplace.json` plus
`.claude-plugin/plugin.json`), and the plugin launches the server with `dotnet dnx Zphil.Roz` pinned
to nuget.org, so nothing is installed by hand. The plugin manifest carries no `env` block;
per-project configuration comes from [`.roz.json`](#project-config-file-rozjson), which exists for
exactly this globally-configured-launcher case. The plugin also ships a skill
(`skills/roz-csharp-editing/SKILL.md`) carrying the same breakage-prevention rules the snippet
delivers (a plugin cannot write a project's rules file, so an on-demand skill is the delivery
vehicle), plus a recommended permission block using the plugin-prefixed tool names.

The server should be registered once: by the plugin or by a project `.mcp.json` entry, not both.
`setup` is plugin-aware. It scans the project's and user's Claude Code settings for a
`roz-mcp@<marketplace>` entry under `enabledPlugins` (nearest scope decides) and states its
verdict. In plugin mode it writes no `.mcp.json` entry, emits the permission rules under the
plugin's tool-name prefix, and puts `--tools` into `.roz.json` instead of an `env` block. A
leftover classic entry that would double-register the server draws a warning; nothing is deleted.
The `--plugin`/`--no-plugin` flags force either mode when the detection is wrong for the project.

### Test-project classification

`TestClassifier` decides whether a project is a test project. It supplements an assembly-reference
heuristic (`IsTestProject`, which looks for test-framework references) with user-configured path
prefixes (`ROZ_TEST_PATHS`) and namespace prefixes (`ROZ_TEST_NAMESPACES`), read from the environment
at startup. The classification drives the `includeTests` defaults across the read tools.

## Diagnostics and analyzers

`get_diagnostics` runs registered `DiagnosticAnalyzer`s through `CompilationWithAnalyzers` alongside
the compiler diagnostics, so analyzer IDs (`xUnit*`, `CA*`, `StyleCop*`) appear next to `CS*` codes.
This is what makes the "Available analyzer fixes" hint fire and what feeds `apply_code_fix`. Per-project
`<NoWarn>` settings and `.editorconfig` severities are honoured via `Project.AnalyzerOptions`.

Analyzers dominate the cost of a diagnostics pass: they run the full analyzer pipeline over a
compilation, far more expensive than fetching compiler diagnostics alone.
`ROZ_DISABLE_ANALYZERS=true` is the escape hatch for a misbehaving analyzer pack (and it explains why
`apply_code_fix` legitimately finds nothing for analyzer-owned IDs when analyzers are disabled).

## Configuration reference

Every environment variable the server reads is registered in
[`RozEnvVars.cs`](src/Zphil.Roz/Infrastructure/RozEnvVars.cs), which is the single source of truth;
the table below is an index.

| Name | Default | Purpose |
|---|---|---|
| `ROZ_TOOLS` | `default` preset (12 tools) | Tool-subset selection; see [Selective tool loading](#selective-tool-loading-roz_tools). |
| `ROZ_SOLUTION_PATH` | unset (CWD walk) | Explicit `.sln`/`.slnx`/`.slnf` path; bypasses discovery. |
| `ROZ_VS_INSTALL_PATH` | unset (`vswhere` auto-selects) | Force a Visual Studio install root for MSBuild registration. |
| `ROZ_LOG_LEVEL` | `Warning` | Minimum Serilog level for the file sink. |
| `ROZ_SESSION_ID` | unset (per-process GUID) | Overrides the client-injected session id in the log tag. |
| `CLAUDE_CODE_SESSION_ID` | set by the client | Session id auto-injected by Claude Code, used for log correlation. |
| `ROZ_IDLE_TIMEOUT_MINUTES` | `30` (`0` disables) | Orphaned-server self-exit window. |
| `ROZ_DISABLE_PARENT_WATCH` | `false` | Disable the Windows parent-PID death watch. |
| `ROZ_DISABLE_AUTO_REFRESH` | `false` | Skip the FileSystemWatcher and entry-time mtime sweep. |
| `ROZ_DISABLE_ANALYZERS` | `false` | Skip analyzer execution in `get_diagnostics`. |
| `ROZ_TEST_PATHS` | empty | Comma/semicolon path-prefix overrides for test-project detection. |
| `ROZ_TEST_NAMESPACES` | empty | Comma/semicolon namespace-prefix overrides for test-project detection. |
| `ROZ_MAX_RESPONSE_CHARS` | `25,000` | Hard response-character cap before truncation. |
| `MAX_MCP_OUTPUT_TOKENS` | set by the MCP client | Fallback char cap (× 2.5) when `ROZ_MAX_RESPONSE_CHARS` is unset; the one registry entry without a `ROZ_` prefix. |

### Project config file (`.roz.json`)

The same variables can be set per project in an optional `.roz.json` file, keyed by the exact
`ROZ_*` names. `Infrastructure/ProjectConfigSeeder.cs` runs as step 0 of server startup (before the
logger initializes, so `ROZ_LOG_LEVEL` and `ROZ_SESSION_ID` from the file take effect) and of
`roz-mcp setup`. It walks up from the working directory, takes the first directory containing the
file (a nested project commits `{}` to shield itself from a parent's config), and seeds each
recognized key into the process environment only when the corresponding variable is unset or blank.
An environment variable always wins.

The file exists for launchers whose command is configured globally rather than per project, such as
a Claude Code plugin: a global launch command carries no per-project `env` block, and the file fills
that gap for every client.

The seedable keys are the `ROZ_`-prefixed subset of the registry, derived programmatically, so a
committed file cannot inject arbitrary environment (`PATH`, `DOTNET_*`, …); unknown keys are skipped
with a warning. Parsing is lenient (comments and trailing commas allowed) and failure-tolerant: an
unparseable file is ignored with a warning and never blocks startup. The seeder runs before Serilog
exists and while stdout is reserved for the MCP protocol, so it never writes to either; its outcome
is logged after logger init, shown in `get_workspace_info`'s `Config file:` line, and reported by
setup's environment checks. A relative `ROZ_SOLUTION_PATH` resolves against the file's directory.

## Operational notes

### Response truncation

`Pipeline/ResponseTruncator.cs` truncates a tool response at the last line boundary and appends a
`--- RESPONSE TRUNCATED ---` footer. The cap resolves in order: `ROZ_MAX_RESPONSE_CHARS` (a positive
integer) → `MAX_MCP_OUTPUT_TOKENS × 2.5` → a 25,000-character default. The verified-write tools compose their verification block against a
truncation budget reduced by the block's own length, so the truncator (which cuts from the end) can
eat neither the block nor the results of the tail operations.

### Logging

Logs are written to a daily-rolling file sink at `%LOCALAPPDATA%/Zphil.Roz/logs/roz-mcp-<date>.log`.
`ROZ_LOG_LEVEL` accepts either Serilog level names (`Verbose`/`Debug`/`Information`/`Warning`/
`Error`/`Fatal`) or `Microsoft.Extensions.Logging.LogLevel` names
(`Trace`/`Debug`/…/`Critical`/`None`); the default is `Warning`.

Every line is stamped with a `[{SessionId}]` field, so lines from multiple server processes sharing
the file (sequential or concurrent client connections) can be told apart. The id resolves in order:
`ROZ_SESSION_ID` → `CLAUDE_CODE_SESSION_ID` → a short per-process GUID. It is resolved once per
process by `Infrastructure/SessionContext.cs`.

### Orphaned-server defenses

An MCP host that dies abnormally can leave a `roz-mcp` process running. Three layers guard against
leaks:

- **`IdleTimeoutWatchdog`**: self-exit after `ROZ_IDLE_TIMEOUT_MINUTES` of no tool activity (default
  30 minutes; `0` disables). It uses a monotonic clock and an in-flight guard so a long-running call
  is never cut off.
- **`ParentProcessWatcher`**: a Windows-only parent-PID death watch via `NtQueryInformationProcess`;
  opt out with `ROZ_DISABLE_PARENT_WATCH=true`. Non-Windows platforms skip it silently, because
  stdin-EOF detection is reliable there.
- **`ServerShutdown`**: the shared exit primitive both watchdogs route through. It disposes the
  `WorkspaceManager` (with a five-second per-disposer bound), flushes Serilog, and calls
  `Environment.Exit(0)`. The unhandled-exception crash handler does *not* route through here: a
  crash flushes and exits on its own path.

## Dependencies

Exact versions live in `src/Zphil.Roz/Zphil.Roz.csproj`, which is the source of truth. The key
packages:

- **`ModelContextProtocol`**: the MCP SDK, with the stdio transport.
- **`Microsoft.CodeAnalysis.CSharp.Workspaces`**: the C# workspace services.
- **`Microsoft.CodeAnalysis.Workspaces.MSBuild`**: `MSBuildWorkspace`, the bridge from a solution
  file to a Roslyn workspace.
- **`Microsoft.Build.Locator`**: locates and registers the MSBuild instance at runtime (see
  [MSBuild bootstrap](#msbuild-bootstrap)).
- **`System.CommandLine`**: the CLI parser (the `setup` subcommand, `--help`, `--version`).
- **`Serilog`** (with the file sink and hosting integration): structured logging to the rolling file
  sink.
- **`Tomlyn`**: reads and writes the Codex CLI's `config.toml` during setup.

The five MSBuild runtime assemblies are referenced but excluded from the package, as described under
[MSBuild bootstrap](#msbuild-bootstrap).
