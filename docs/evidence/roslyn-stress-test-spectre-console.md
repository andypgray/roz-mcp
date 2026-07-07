> **Historical record.** Written 2026-04-05 against the tool as it existed then. The server was
> named `roslyn-mcp` before the 2026-07 rename to `roz-mcp`, so the tool names, tool counts, and
> preset sizes in the text below are period-correct for that date, not the current tool surface.
> Published July 2026 verbatim except for the redaction of internal file paths. A few links to
> internal working files (`../backlog/`, run-output `results/`, pre-rename source paths) do not
> resolve in this repository and are left as written rather than rewritten. Bugs identified
> here were subsequently fixed. See the evidence index at [README.md](README.md) for how this
> document maps to the project's current claims.

# Spectre.Console Stress Test — 2026-04-05

Stress-tested the Roslyn MCP server against [spectreconsole/spectre.console](https://github.com/spectreconsole/spectre.console) — a modern C# console widget library chosen for: records with positional parameters (`record class`, `readonly record struct`), preprocessor `#if` gating for polyfills, 4-TFM multi-targeting, and extension-method-dominated fluent API.

**Target:** 8 logical projects, 29 TFM variants, 503 files in main project, C# 14.0.
- Main lib TFMs: `net10.0 | net8.0 | net9.0 | netstandard2.0` (4 TFMs — doubles the Orleans 2-TFM regression test)
- Test projects: 3 TFMs each
- Source generator project: netstandard2.0

**Workspace health:** 71 CS compile errors (100 diagnostics total across all severities). Root cause: source-generator output (Color.Red/Blue/Grey constants, Spinner.Known, Emoji._emojis) is not present in the workspace's compilation — the generator hasn't run. This baseline state is pre-existing and load-bearing for no findings.

**Method:** ~80 tool calls across 12 categories. All edits reverted; MD5 round-trip verified on all modified files. Final `git status --porcelain` identical to starting baseline.

## Test matrix

| Tool | Status | Notes |
|------|--------|-------|
| `get_workspace_info` | Works | 4-TFM display correct: `net10.0 \| net8.0 \| net9.0 \| netstandard2.0`; no inflation |
| `find_symbol` | Works (cosmetic issues) | **Positional record params missing from signature header** (bug #3); **`readonly` dropped on `readonly record struct`** (bug #4); `this` dropped on extension methods (bug #5) |
| `get_symbols_overview` | Works (cosmetic issues) | `{ get; init; }` accessor info not shown; `[Flags]` attribute omitted from enum headers |
| `go_to_definition` | Works (with gap) | **`this` parameter dropped when showing substituted generic extension** (bug #6) |
| `find_references` | Works (dedup issue) | **`+N in test projects` count inflated 12-15×** when `includeTests=false` (bug #1) |
| `find_callers` | Works | `.ctor` special name works; per-line dedup vs `find_references` differs |
| `find_implementations` | Works | 28 impls of `IRenderable.Render` — no TFM inflation |
| `find_derived_types` | Works | Tree format for classes, flat for interfaces; `includeBody=true` switches display |
| `find_overloads` | Works (with gap) | **Silently picks wrong class when `containingType` is ambiguous** (bug #2); no default value display |
| `get_type_hierarchy` | Works | Record keyword NOT shown for record types; correct base-type chain for deep inheritance |
| `rename_symbol` | Works | **Positional record parameter rename correctly propagates to synthesized properties + XML `<param>` tags**; **cannot rename symbols in inactive `#if` branches** (intentional) |
| `replace_symbol` | Works | `init` accessor preserved; MD5 round-trip clean |
| `insert_symbol` | Works (cosmetic) | No blank line between inserted and adjacent members |
| `remove_symbol` | Works (bug) | **Strips trailing comma from previous enum value** on last-item removal (bug #7) |
| `replace_content` | Works | No-op detection works; CRLF/LF handling not tested (file already LF) |
| `add_usings` | Works | File-scoped namespace handled; no blank line inserted between using and namespace |
| `remove_unused_usings` | Works | Correctly ignores `global using`s from Properties/Usings.cs |
| `get_diagnostics` | Works | No TFM inflation — 71 errors reported once, not 284 |
| `reset_baseline` | Works | Captures all severities (100) regardless of severity filter |
| `reload_workspace` | Works | 29 projects, 2516 documents |

## Bugs

### 1. `find_references`/`find_callers` inflate "test projects" summary count 12-15× when `includeTests=false` (HIGH)

The summary line `(+N in test projects)` reported by `find_references` and `find_callers` when `includeTests=false` is wildly inflated compared to the actual number of references that appear when `includeTests=true`.

**Reproduction:**

```
# Style struct — without includeTests
find_references(symbolName: "Style", kind: Struct, maxResults: 5)
→ Total: 278 across 4 projects
  (+864 in test projects)

# Same query with includeTests=true
find_references(symbolName: "Style", kind: Struct, maxResults: 5, includeTests: true)
→ Total: 334 across 6 projects
  Distribution:
    Spectre.Console.Ansi.Tests   34  (2 files)
    Spectre.Console.Tests        22  (5 files)
    (test total: 56)

# Reported 864 in test projects. Actual: 56. Ratio: 15.4×
```

```
# IRenderable — without includeTests
find_references(symbolName: "IRenderable", kind: Interface, maxResults: 5)
→ Total: 205 across 5 projects
  (+192 in test projects)

# Same query with includeTests=true
find_references(symbolName: "IRenderable", kind: Interface, maxResults: 5, includeTests: true)
→ Total: 221 across 6 projects
  Distribution:
    Spectre.Console.Tests   16  (5 files)
    (test total: 16)

# Reported 192 in test projects. Actual: 16. Ratio: 12×
```

**Context:** Spectre.Console.Tests (3 TFMs) and Spectre.Console.Ansi.Tests (3 TFMs) give 6 test-project variants. A 3× inflation would be explained by per-TFM accumulation; a 6× inflation would be per-variant. But the observed 12-15× ratios exceed both, suggesting references are being counted once per {test-project-variant × dependency-project-variant} pair rather than deduplicated to unique source-location pairs.

**Expected:** The `(+N in test projects)` summary should match the count that would be visible with `includeTests=true` — specifically, unique source-location references in test projects (dedup'd across TFM variants).

**Affects:** User trust in count numbers. A developer seeing "+864 in test projects" will massively overestimate test coverage/usage impact. Does not affect the `find_references` or `find_callers` result body, which only show main-project refs when `includeTests=false`.

---

### 2. `find_overloads` silently picks wrong class on ambiguous `containingType` (MED)

When two classes share the same name, `find_overloads` picks one arbitrarily without disambiguation warning. This is inconsistent with `find_references`, which correctly reports ambiguity.

**Reproduction:**

Spectre.Console has `public sealed class Markup` at `Spectre.Console\Widgets\Markup.cs:7` (the real widget class with a constructor `Markup(string text, Style? style = null)`).

Spectre.Console.Tests has `public sealed class Markup` at `Spectre.Console.Tests\Unit\AnsiConsoleTests.Markup.cs:5` (a nested test-fixture class inside `AnsiConsoleTests`, with only a parameterless default constructor).

```
find_overloads(symbolName: ".ctor", containingType: "Markup")
→ "'.ctor' in Markup has no overloads (only 1 signature):
    1. () — Spectre.Console.Tests\Unit\AnsiConsoleTests.Markup.cs:5"

# Wrong class — found the test fixture, not the real Markup widget
```

Contrast with `find_references`, which detects ambiguity when two `StringExtensions` classes exist:

```
find_references(symbolName: "ReplaceExact", containingType: "StringExtensions")
→ "Ambiguous: 2 overloads of 'ReplaceExact' in 'StringExtensions'.
    Drop symbolName and use filePath+line+column to select a specific overload:
    [method] ... — Spectre.Console.Ansi\Utilities\StringExtensions.cs:28
    [method] ... — Spectre.Console\Extensions\Bcl\StringExtensions.cs:126"
```

The `find_overloads` path resolves the `Markup` name → picks one `Markup` class silently → returns its constructors. The `find_references` path detects two methods with the same containing-type name and reports the ambiguity.

**Workaround:** Use `filePath` + `line` + `column` to disambiguate. `find_overloads` with exact position correctly returned `(string text, Style? style) — Spectre.Console\Widgets\Markup.cs:40`.

**Expected:** `find_overloads` should either (a) report ambiguity like `find_references`, (b) list overloads from both classes, or (c) prefer the class with the matching visible namespace/kind.

---

### 3. `find_symbol` omits positional record parameters from signature header (MED — cosmetic)

Records declared with positional parameters display their `: IEquatable<T>` clause but drop the `(Param1 Type1, Param2 Type2)` positional parameter list in the 1-line signature header. The positional parameters DO appear in the body when `includeBody=true`, creating an inconsistency.

**Reproduction:**

```
# Source (RenderOptions.cs:8):
public record class RenderOptions(IReadOnlyCapabilities Capabilities, Size ConsoleSize)

# Signature header
find_symbol(names: ["RenderOptions"], kind: Class)
→ "public record class RenderOptions : IEquatable<RenderOptions>
    Location: Spectre.Console\Rendering\RenderOptions.cs:8
    9 members (8 properties, 1 method)"

# With includeBody=true, positional params appear
find_symbol(names: ["RenderOptions"], kind: Class, includeBody: true, maxBodyLines: 3)
→ "public record class RenderOptions : IEquatable<RenderOptions>
    Body:
      public record class RenderOptions(IReadOnlyCapabilities Capabilities, Size ConsoleSize)
      {
          ..."
```

**Expected:** Either include positional parameters in the header (`public record class RenderOptions(IReadOnlyCapabilities Capabilities, Size ConsoleSize) : IEquatable<RenderOptions>`) or omit them from the body to be consistent. The synthesized properties ARE listed among the members, but they're not distinguished from normal init-only properties — a user reading the signature cannot tell which properties come from the primary constructor vs which were declared inside the body.

---

### 4. `readonly` modifier dropped on `readonly record struct` / `readonly struct` (MED — cosmetic)

```
# Source (Style.cs:6):
public readonly record struct Style

# find_symbol output
find_symbol(names: ["Style"], kind: Struct)
→ "public record struct Style : IEquatable<Style>"

# get_symbols_overview output
get_symbols_overview(filePaths: ["Spectre.Console.Ansi/Style.cs"])
→ "public record struct Style : IEquatable<Style>  :6"

# get_type_hierarchy output
get_type_hierarchy(symbolName: "Style", kind: Struct)
→ "Type hierarchy for 'Style':
    Base types:
      ValueType (external)
    Implemented interfaces:
      IEquatable<Style> (external)"
```

All three display paths drop `readonly`. No tool surfaces that `Style` is a readonly value type. Similarly, `get_type_hierarchy` does not indicate it is a `record` type — neither "record" nor "readonly" appears anywhere.

**Expected:** Show `readonly` modifier in all signature displays. Surface `record` keyword in `get_type_hierarchy`.

---

### 5. Extension methods drop `this` modifier from signature (MED — cosmetic)

All static extension methods (very heavily used in Spectre.Console's fluent API) display without their `this` parameter modifier, making it impossible to tell from the signature alone whether a method is an extension.

**Reproduction:**

```
# Source (IHasTableBorder.cs:254):
public static T Border<T>(this T obj, TableBorder border)
    where T : class, IHasTableBorder

# find_symbol output
find_symbol(names: ["Border"])
→ "public static method T Border<T>(T obj, TableBorder border) where T : class, IHasTableBorder
    Containing type: HasTableBorderExtensions"
```

The `Containing type: HasTableBorderExtensions` (a `static class`) is a hint, but the `this T obj` parameter is rendered as bare `T obj`.

Similarly, `AddColumns(params TableColumn[] columns)` displays without the `params` modifier.

**Expected:** Preserve `this` and `params` modifiers in extension method signatures.

---

### 6. `go_to_definition` on generic extension method drops `this` parameter when substituting type arguments (MED)

When resolving a fluent-chain call like `table.Expand()` on a generic extension, the tool shows the substituted return type (good) but drops the entire `this T obj` parameter (confusing).

**Reproduction:**

Call site in `Columns.cs:99`:
```csharp
table.Expand();
```

Source (`IExpandable.cs:43`):
```csharp
public static T Expand<T>(this T obj)
    where T : class, IExpandable
```

```
go_to_definition(filePath: "Spectre.Console\\Widgets\\Columns.cs", line: 99, column: 20)
→ "public method Table Expand<T>() where T : class, IExpandable
    Location: Spectre.Console\IExpandable.cs:43
    Containing type: ExpandableExtensions"
```

Problems:
- `Table Expand<T>()` implies the return type is `Table` (substituted from `T`) AND takes no parameters — but the real signature has `(this T obj)`.
- Drops `static` modifier.
- Mixes substituted (`T → Table`) with unsubstituted (`<T> where T : ...`) — a hybrid that doesn't match any valid C# declaration.

**Expected:** Either show the unsubstituted declaration `public static T Expand<T>(this T obj) where T : class, IExpandable`, or show fully substituted `public static Table Expand(this Table obj)` — not a mix.

---

### 7. `remove_symbol` strips trailing comma from previous enum value on last-item removal (LOW)

Inserting a new enum value at the end, then removing it, produces a file that differs from the original — the previous value loses its trailing comma.

**Reproduction:**

```
# Original Justify.cs:
public enum Justify
{
    Left = 0,
    Right = 1,
    Center = 2,   ← trailing comma present
}

# Step 1: insert_symbol new value after Center
insert_symbol(filePath: "Spectre.Console/Justify.cs",
              symbolName: "Center",
              position: "After",
              content: "\n    /// <summary>\n    /// Justified (test).\n    /// </summary>\n    Justified = 3,\n")

# After insert:
    Center = 2,
    /// <summary>
    /// Justified (test).
    /// </summary>
    Justified = 3

# Step 2: remove_symbol removes Justified
remove_symbol(filePath: "Spectre.Console/Justify.cs",
              symbolName: "Justified",
              containingType: "Justify")

# After remove:
    Center = 2    ← trailing comma GONE
}
```

The `Center = 2,` loses its trailing comma during `remove_symbol`. The insert+remove pair does NOT round-trip.

For reference, the same insert+remove pattern on PROPERTIES (tested on RenderOptions.cs) DOES round-trip cleanly (MD5 verified). The bug is specific to enum members, where the "last item may or may not have trailing comma" convention introduces a formatting decision.

**Workaround:** Use `replace_content` to restore the trailing comma.

**Expected:** `remove_symbol` should preserve trailing commas on the preceding enum value to make insert+remove round-trip cleanly, or document this as intentional "canonical form" behavior.

---

## Positive confirmations

### Multi-TFM deduplication — main-project results clean (4 TFMs scaling validation)

The Orleans follow-up fix (2026-03-30) for 2-TFM dedup holds up at 4 TFMs in the main project. All of the following report each symbol/reference exactly once, NOT 4×:

- `get_workspace_info`: "8 logical, 26 total" — TFMs displayed as joined list: `net10.0 | net8.0 | net9.0 | netstandard2.0`
- `find_symbol` on `IRenderable` (interface): 1 result
- `find_symbol` on `Renderable` (class): 25 results (not 100)
- `find_symbol` on `Markup`: 45 results across 3 logical projects
- `find_references` on `IRenderable`: 205 refs across 74 files, 5 projects
- `find_references` on `Style` struct: 278 refs across 55 files
- `find_implementations` on `IRenderable.Render`: 28 implementations
- `find_derived_types` on `Renderable`: 27 derived classes
- `find_callers` on `Table.AddColumn` (instance): 5 main-project callers
- `get_diagnostics`: 71 errors total, not 284 (= 71 × 4)

The only observed dedup bug is the test-project count inflation documented in bug #1.

---

### Positional record parameter rename — correctly updates synthesized properties AND XML doc comments

`rename_symbol` on the `Capabilities` positional parameter of `record class RenderOptions(IReadOnlyCapabilities Capabilities, Size ConsoleSize)` correctly updated all of:

1. The primary constructor parameter name itself (line 8)
2. `<param name="Capabilities">` in the XML doc comment (line 6)
3. Synthesized property references: `Capabilities.ColorSystem`, `Capabilities.Ansi`, `Capabilities.Unicode` (lines 13, 18, 23 within RenderOptions.cs)
4. External references in other files (1 file: LiveRenderable.cs)

```
rename_symbol(filePath: "Spectre.Console/Rendering/RenderOptions.cs",
              symbolName: "Capabilities", line: 8, column: 60,
              newName: "CapabilitiesTest")
→ "Renamed 'Capabilities' to 'CapabilitiesTest'.
    Changed 2 file(s):
      Spectre.Console\Live\LiveRenderable.cs
      Spectre.Console\Rendering\RenderOptions.cs"
```

After reverse-rename, MD5 matches the original byte-for-byte. This is a high bar and it was met.

---

### `rename_symbol` correctly refuses symbols in inactive `#if` branches

```
# CancellationTokenHelpers class is entirely inside #if NETSTANDARD2_0.
# When primary compilation is net10.0, that class is in disabled code.
rename_symbol(filePath: "Spectre.Console/Internal/Polyfill/CancellationToken.cs",
              symbolName: "CancelAsync", line: 6, column: 24,
              newName: "CancelAsyncTest")
→ "No symbol found at exact position ...:6:24 — in disabled code (#if false).
    Cursor must be on the symbol's identifier token."
```

Note the clear diagnostic message. However, `find_references` on the SAME symbol DOES return results (3 references to `CancellationTokenHelpers.CancelAsync`) — so read-only queries see disabled-code symbols but edit tools cannot target them. This asymmetry is documented in `find_callers` / `find_references` server instructions ("forgiving" vs "strict" position resolution) and the rename error is clearly explained.

---

### Preprocessor `#if` handling — active-branch rename works cleanly

A symbol defined in an `#if NETSTANDARD2_0` branch WITH callers in `#else` and active code. `rename_symbol` on `StringExtensions.ReplaceExact` (defined outside `#if`, with a body wrapped in `#if/#else`):

```
rename_symbol(symbolName: "ReplaceExact", line: 126, column: 29, newName: "ReplaceExactTest")
→ "Renamed 'ReplaceExact' to 'ReplaceExactTest'.
    Changed 4 file(s)"
```

All 7 active-branch call sites updated. Reverse-rename cleanly restored the file.

---

### Compiler-generated record members correctly filtered

`find_symbol` with `containingType: "RenderOptions"` does NOT expose `Deconstruct`, `EqualityContract`, `PrintMembers`, `<Clone>$`, `op_Equality`, etc. These compiler-synthesized members stay hidden as expected:

```
find_symbol(names: ["Deconstruct", "EqualityContract", "PrintMembers"], containingType: "RenderOptions")
→ "No symbols found matching 'Deconstruct' in type 'RenderOptions'."
   (etc.)
```

**Minor limitation:** `find_symbol(names: ["<Clone>$"])` is rejected with "Generic FQN syntax is not supported" — the compiler-generated symbol name `<Clone>$` collides with the generic arity syntax (`<>`). Users cannot query synthesized clone method by name. This is an edge case and `containingType` filtering + the correct absence of compiler-gen members mean it doesn't matter in practice.

---

### Edit round-trips verified byte-for-byte (MD5)

| File | Operation | MD5 round-trip |
|------|-----------|----------------|
| `RenderOptions.cs` | `replace_symbol` on init-only property (get;init; ↔ get;set; ↔ get;init;) | ✓ |
| `RenderOptions.cs` | `rename_symbol` on positional parameter (+5 rename propagations) | ✓ |
| `RenderOptions.cs` | `insert_symbol` of new init property + `remove_symbol` | ✓ |
| `Justify.cs` | `add_usings` + `remove_unused_usings` | ✓ |
| `Justify.cs` | `insert_symbol` Before (comment line) + `replace_content` revert | ✓ |
| `Justify.cs` | `rename_symbol` on enum value across 13 files + reverse rename | ✓ |
| `StringExtensions.cs` | `rename_symbol` across 4 files + reverse rename | ✓ |

---

## Cosmetic observations

### `[Flags]` attribute missing from enum headers

```
# Source (Decoration.cs:9-11):
[Flags]
public enum Decoration

# find_symbol output (header):
"public enum Decoration"

# get_symbols_overview output:
"public enum Decoration  :10"

# With includeBody=true, [Flags] appears in the body.
```

Affects all `[Flags]` enums (Decoration, etc.). Users can't distinguish flags enums from regular enums via signature alone.

---

### Method signatures drop optional-parameter default values

```
# Source (Markup.cs:40):
public Markup(string text, Style? style = null)

# find_overloads output:
"1. (string text, Style? style) — Spectre.Console\Widgets\Markup.cs:40"
```

The `= null` default value is consistently dropped across `find_overloads`, `find_symbol`, `go_to_definition`. Same for the positional record param `capabilities = null` in `RenderOptions.Create`.

---

### NuGet package contentFiles paths use `.\\..\\..\\..\\..` traversal

The `get_symbols_overview(project: "SourceGenerator")` output includes NuGet package sources with escaped relative paths:

```
=== ..\..\..\..\..\.nuget\packages\isexternalinit\1.0.3\contentFiles\cs\netstandard2.0\IsExternalInit\IsExternalInit.cs ===
```

And `find_symbol(names: ["*Extensions"])` returned multiple entries for NuGet content files duplicated per-TFM:

```
29. Wcwidth.IntegerExtensions — ..\..\..\..\..\.nuget\packages\wcwidth.sources\4.0.1\contentFiles\cs\net10.0\...\IntegerExtensions.cs:3
30. Wcwidth.IntegerExtensions — ..\..\..\..\..\.nuget\packages\wcwidth.sources\4.0.1\contentFiles\cs\net9.0\...\IntegerExtensions.cs:3
```

These are not duplicate-symbol bugs — the NuGet `.Sources` package genuinely includes one physical .cs file per TFM in its `contentFiles` folder, and each is added to the project via `<Compile Include>`. The paths rendered this way are confusing but correct.

---

### `get_type_hierarchy` shows 2-level base chain but no `System.Object`

```
get_type_hierarchy(symbolName: "Grid", kind: Class)
→ "Base types:
     JustInTimeRenderable
       Renderable
   Implemented interfaces:
     IExpandable, IRenderable"
```

Good: shows full depth (Grid → JustInTimeRenderable → Renderable).
Missing: System.Object (likely intentional — noise reduction).
Missing: `record` keyword when base type is a record.

---

### Enum insertion forgets blank lines between members

```
# Original Justify.cs:
    Left = 0,
                    ← blank line
    Right = 1,
                    ← blank line
    Center = 2,

# After insert_symbol After "Center":
    Center = 2,
    /// <summary>    ← no blank line between Center and new member
    /// Justified (test).
    /// </summary>
    Justified = 3
```

The inserted content is placed immediately adjacent to the anchor, whereas the existing style has blank lines between members. Users would need to manually add blank lines or re-format.

---

## Revert verification

```bash
# Before testing: git status --porcelain → 763 lines
# After testing:  git status --porcelain → 763 lines
# diff: no changes

# All 7 modified files round-trip MD5-verified:
81d40df8d7ef92319b77a10010fec99c  Spectre.Console/Rendering/RenderOptions.cs  ✓
2c958ef0740b79bfa6cad4c304238ae5  Spectre.Console.Ansi/Style.cs               ✓ (untouched)
17bdbee3ad7e22e55eacb97fa9c2cb29  Spectre.Console/Justify.cs                  ✓
a69acae20c2c17e2a169e69cf4228603  Spectre.Console/Extensions/Bcl/StringExtensions.cs ✓
```

No modifications left on disk. Workspace verified clean.
