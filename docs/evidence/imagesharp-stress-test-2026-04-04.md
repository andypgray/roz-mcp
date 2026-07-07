> **Historical record.** Written 2026-04-04 against the tool as it existed then. The server was
> named `roslyn-mcp` before the 2026-07 rename to `roz-mcp`, so the tool names, tool counts, and
> preset sizes in the text below are period-correct for that date, not the current tool surface.
> Published July 2026 verbatim except for the redaction of internal file paths. A few links to
> internal working files (`../backlog/`, run-output `results/`, pre-rename source paths) do not
> resolve in this repository and are left as written rather than rewritten. Bugs identified
> here were subsequently fixed. See the evidence index at [README.md](README.md) for how this
> document maps to the project's current claims.

# ImageSharp Stress Test — 2026-04-04

Stress-tested the Roslyn MCP server against [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) — a pure-managed image processing library chosen for operator overloads at scale, self-referential generic constraints (`IPixel<TSelf> where TSelf : IPixel<TSelf>`), `ref struct`/`Span<T>` patterns, and `unsafe` code.

**Target:** 1 project, 1271 files, C# 12.0, net8.0, nullable=enable.

**Workspace health:** 7,379 compilation errors across 810 files (dominated by CS0246 — 4,441 type-not-found errors). Root cause: missing NuGet restore or shared infrastructure in this throwaway clone. This pre-existing state is load-bearing for bugs #1 and #2.

**Method:** ~65 tool calls across 12 categories. All edits reverted after testing.

## Test matrix

| Tool | Status | Notes |
|------|--------|-------|
| `get_workspace_info` | Works | Clean single-project output |
| `find_symbol` | Works (with gaps) | **Cannot find types that have compilation errors** (bug #1) |
| `get_symbols_overview` | Works (cosmetic issues) | Missing `ref` on ref structs, drops `ref`/`out`/`in` param modifiers |
| `go_to_definition` | Works | Correctly resolves type parameters, operators, extension methods |
| `find_references` | Works | Found 586 refs for `Image<>` across 188 files |
| `find_callers` | Works (with gap) | **Cannot trace conversion operator usage** (bug #3) |
| `find_implementations` | Works | Found 29 `IPixel` impls — 4 more than `find_derived_types` (bug #2) |
| `find_derived_types` | Works (with gap) | **Silently drops types with compilation errors** (bug #2) |
| `find_overloads` | Works (cosmetic issue) | Conversion operator overloads indistinguishable — no return type shown |
| `get_type_hierarchy` | Works | Self-referential `IPixel<RgbaVector>` displays correctly |
| `rename_symbol` | Works | Correctly updates inactive `#if` branches with explicit note |
| `replace_symbol` | Works | Expression body → block body on operators works |
| `remove_symbol` | Works | Operator removal clean |
| `insert_symbol` | Works | Operator insertion clean |
| `replace_content` | Works | No-op detection, regex mode, literal mode all work |
| `add_usings` | Works | Duplicate detection works |
| `remove_unused_usings` | Works | Round-trip with `add_usings` verified |
| `get_diagnostics` | Works (cosmetic issue) | **Incremental "resolved" count misleading with `filePaths` filter** (bug #4) |
| `reload_workspace` | Works | Clean reload after manual file restore |
| `reset_baseline` | Works | Correct baseline capture |

## Bugs

### 1. `find_symbol` cannot find types with compilation errors, but other tools can (MEDIUM)

Types with compilation errors are invisible to `find_symbol` but visible to `get_symbols_overview`, `get_type_hierarchy`, `find_implementations`, and even `find_symbol` itself when searching for their *members*.

**Affected types:** `Rgba32`, `Rgb24`, `Rgb48`, `Rgba64` — all have CS0315 or CS0103 errors.

**Reproduction:**

```
# find_symbol returns nothing
find_symbol(names: ["Rgba32"], kind: Struct, matchMode: Exact)
→ "No symbols found matching 'Rgba32' with kind 'Struct' (matchMode=Exact)."

# But get_symbols_overview sees it fine (syntax-tree based)
get_symbols_overview(filePaths: ["PixelFormats/PixelImplementations/Rgba32.cs"], depth: 0)
→ "public partial struct Rgba32 : IPixel<Rgba32>, IPackedVector<uint>  :25
   45 members (6 fields, 6 constructors, 4 properties, 26 methods, 3 operators)"

# get_type_hierarchy resolves it when given file+line
get_type_hierarchy(symbolName: "Rgba32",
                   filePath: "PixelFormats/PixelImplementations/Rgba32.cs",
                   line: 25)
→ shows ValueType base, IPixel<Rgba32>, IPackedVector<uint>, IPixel, IEquatable<Rgba32>

# find_symbol even finds MEMBERS of the invisible type
find_symbol(names: ["op_Implicit"], containingType: "Rgba32", kind: Operator)
→ "public static operator implicit operator Rgba32(Rgb color)
    Location: PixelFormats\PixelImplementations\Rgba32.cs:189"

# The error causing invisibility
get_diagnostics(filePaths: ["PixelFormats/PixelImplementations/Rgba32.cs"], severity: Error)
→ "Rgba32.cs:25: error CS0315: The type 'uint' cannot be used as type parameter 'TPacked'
   in the generic type or method 'IPackedVector<TPacked>'.
   There is no boxing conversion from 'uint' to 'IEquatable<uint>'."
```

**Root cause hypothesis:** `find_symbol` uses the semantic model which skips errored type declarations, while `get_symbols_overview` uses the syntax tree, and `find_implementations` traverses from member declarations which can still resolve despite the containing type's error.

**Expected:** `find_symbol` should find the type (possibly with an error indicator), or at minimum be consistent with `find_implementations` and `get_symbols_overview`.

---

### 2. `find_derived_types` silently drops types with compilation errors (MEDIUM)

`find_derived_types` on `IPixel<TSelf>` returns 25 implementations. `find_implementations` on `IPixel.CreatePixelOperations` returns 29. The 4 missing types are exactly those with compilation errors.

**Reproduction:**

```
find_derived_types(symbolName: "IPixel",
                   filePath: "PixelFormats/IPixel.cs",
                   line: 14,
                   maxResults: 40)
→ 25 types: NormalizedShort2, NormalizedShort4, Short4, Rg32, Short2, Argb32,
  Rgba1010102, Byte4, L8, Bgr24, HalfSingle, A8, Bgra5551, HalfVector4,
  HalfVector2, Bgr565, Bgra32, RgbaVector, Bgra4444, La32, NormalizedByte4,
  L16, La16, NormalizedByte2, Abgr32

find_implementations(symbolName: "CreatePixelOperations",
                     containingType: "IPixel",
                     maxResults: 40)
→ 29 types: same 25 + Rgba32, Rgb24, Rgb48, Rgba64

# All 4 missing types have compilation errors
get_diagnostics(filePaths: ["PixelFormats/PixelImplementations/Rgba32.cs",
                            "PixelFormats/PixelImplementations/Rgb24.cs",
                            "PixelFormats/PixelImplementations/Rgb48.cs",
                            "PixelFormats/PixelImplementations/Rgba64.cs"],
                severity: Error)
→ 9 errors (CS0315 on Rgba32/Rgba64, CS0103 on Rgb24/Rgb48/Rgba64)
```

**Expected:** Either consistent results between the two tools, or `find_derived_types` should include a note such as "+4 type(s) may implement this interface but were excluded due to compilation errors" — similar to how `find_implementations` already notes "+N in metadata/framework assemblies."

---

### 3. `find_callers` cannot trace conversion operator usage (LOW)

Both `op_Implicit` and `op_Explicit` return no callers, even though these operators are used extensively throughout the codebase. Non-conversion operators like `op_Multiply` ARE found by `find_references`.

**Reproduction:**

```
# Implicit conversion — no callers found
find_callers(symbolName: "op_Implicit", containingType: "Point", maxResults: 10)
→ "No callers found for 'op_Implicit'."

# Explicit conversion — no callers found
find_callers(symbolName: "op_Explicit", containingType: "Point", maxResults: 10)
→ "No callers found for 'op_Explicit'."

# But arithmetic operators ARE found by find_references
find_references(symbolName: "op_Multiply", containingType: "ColorMatrix", maxResults: 5)
→ 4 references in Processing\KnownFilterMatrices.cs
```

**Root cause hypothesis:** Implicit/explicit conversions are compiler-inserted calls. Roslyn's `FindReferencesAsync` may not track these. This may be a Roslyn API limitation rather than an MCP server bug.

**Expected (suggestion):** Instead of bare "No callers found", add guidance: "Note: implicit/explicit conversion operators are invoked by the compiler and may not appear as direct call sites. Use `find_references` instead."

---

### 4. Incremental diagnostics "resolved" count misleading with `filePaths` filter (LOW)

When `get_diagnostics` is called with both `incremental: true` and a `filePaths` filter, the "resolved" count includes diagnostics from files OUTSIDE the filter — as if they were fixed.

**Reproduction:**

```
# Set baseline (2582 diagnostics across all files)
reset_baseline()
→ "2582 diagnostic(s) captured as new baseline"

# Make a trivial edit to ONE file
add_usings(filePath: "Primitives/ColorMatrix.cs", usings: ["System.Diagnostics"])

# Filtered incremental check — wildly inflated "resolved" count
get_diagnostics(incremental: true, filePaths: ["Primitives/ColorMatrix.cs"])
→ "0 new, 2581 resolved, 1 unchanged"
#          ^^^^ this is (baseline total - diagnostics in this file), NOT actually resolved

# Unfiltered incremental check — correct
get_diagnostics(incremental: true)
→ "0 new, 0 resolved, 2582 unchanged"
```

**Root cause:** The incremental diff compares the filtered result set against the full baseline. Diagnostics from other files are absent from the filtered results, so they count as "resolved."

**Expected:** Either scope the resolved/unchanged counts to the same file filter, or add a note: "(scoped to 1 file; baseline contains 2582 diagnostics across all files)."

## Cosmetic issues

### `ref struct` displayed as plain `struct`

`RowOctet<T>` is declared as `internal ref struct RowOctet<T>` (source line 15) but all tools drop the `ref` modifier.

```
# Source (RowOctet.cs:15):
internal ref struct RowOctet<T> where T : struct

# find_symbol output:
find_symbol(names: ["RowOctet"], kind: Struct, matchMode: Exact)
→ "internal struct RowOctet<T> where T : struct"
#           ^^^^^^ missing "ref"
```

### `ref`/`out`/`in` parameter modifiers dropped from display

`Block8x8F.Quantize` parameters are all `ref` in source but displayed without modifiers.

```
# Source (Block8x8F.cs:277):
public static void Quantize(ref Block8x8F block, ref Block8x8 dest, ref Block8x8F qt)

# find_symbol output:
find_symbol(names: ["Quantize"], containingType: "Block8x8F", kind: Method)
→ "void Quantize(Block8x8F block, Block8x8 dest, Block8x8F qt)"
#               ^^^ all three "ref" modifiers missing
```

### Operator display has redundant `operator` keyword

Conversion operators show `operator` twice — once in the `[public static operator]` tag and once in the C# syntax:

```
find_symbol(names: ["op_Implicit"], containingType: "DenseMatrix", kind: Operator)
→ "public static operator implicit operator DenseMatrix<T>(T[,] data)"
#              ^^^^^^^^          ^^^^^^^^ redundant
```

Expected: `public static implicit operator DenseMatrix<T>(T[,] data)` — matching actual C# syntax.

### `find_overloads` on conversion operators doesn't show return types

For conversion operators, the return type IS the distinguishing feature. Currently both overloads display identically:

```
find_overloads(symbolName: "op_Implicit", containingType: "Point")
→ 1. (Point point) — Primitives\Point.cs:80
  2. (Point point) — Primitives\Point.cs:87

# These are actually:
#   implicit operator PointF(Point point)   ← returns PointF
#   implicit operator Vector2(Point point)  ← returns Vector2
# Impossible to distinguish without reading the source.
```

Suggested format: `1. -> PointF (Point point)` or `1. PointF (Point point)`.

## What works well

### Operator overloads are first-class citizens

`find_symbol` with `op_*` names found all 50 operator declarations across the codebase. `find_overloads` correctly grouped overloads by containing type (e.g. 2 `op_Multiply` on `ColorMatrix`). `go_to_definition`, `find_references`, `replace_symbol`, `insert_symbol`, and `remove_symbol` all worked correctly on operators.

### `rename_symbol` handles inactive preprocessor branches

Renaming `HotPath` → `HotPathOption` in `InliningOptions.cs` updated 4 files and explicitly noted:
```
Note: 1 occurrence(s) in inactive preprocessor branches (#if/#else) were updated via text replacement.
```
Both the `#if PROFILING` and `#else` branches were updated correctly — verified by file read.

### `rename_symbol` correctly rejects operators and indexers

Clear, informative error messages:
```
rename_symbol(symbolName: "op_Addition", kind: Operator) → "Operator 'op_Addition' cannot be renamed — operator names are determined by the operator keyword."
rename_symbol(symbolName: "this[]", kind: Indexer)       → "Indexers cannot be renamed — the indexer name 'this[]' is fixed by the language."
```

### Open generic arity disambiguation

`Image<>` (arity 1) vs `Image` (non-generic) resolved correctly. The ambiguity error when using bare `Image` provides excellent guidance:
```
Ambiguous: 2 symbols match 'Image'. Use containingType to narrow...
These differ by generic arity. Use open generic syntax to disambiguate:
'Image' (non-generic), 'Image<>' (arity 1).
```

### Self-referential generics display correctly

`get_type_hierarchy` on `RgbaVector` shows `IPixel<RgbaVector>`. The interface declaration `IPixel<TSelf> : IPixel, IEquatable<TSelf> where TSelf : unmanaged, IPixel<TSelf>` renders in full with no truncation.

### `find_derived_types` tree display

The `CloningImageProcessor` hierarchy rendered as an indented tree:
```
AffineTransformProcessor
├─ RotateProcessor
└─ SkewProcessor
CropProcessor
ProjectiveTransformProcessor
ResizeProcessor
```

### Constructor and destructor special names

`.ctor` found constructors with correct overload discovery (7 `Argb32` constructors). `Finalize` found destructors and displayed them with `~ClassName()` syntax. `find_overloads` on `.ctor` includes the implicit parameterless constructor.

### Indexer support

`find_symbol` for `this[]` found 18 indexers with accessor info (get/set). `find_overloads` correctly grouped multi-signature indexers. `find_callers` on `Block8x8F.this[]` found 15 callers with correct accessor-level tracking.

### `find_callers` with `includeOverloads: true`

Correctly aggregated callers across all 3 `Color` constructor overloads with a clear "all 3 overloads" header.

### Edit operation round-trip

`insert_symbol` → `remove_symbol` on operators, `add_usings` → `remove_unused_usings`, and `replace_symbol` (expression body → block body) all worked correctly. Incremental diagnostics confirmed no new errors introduced by edits.

### `go_to_definition` on type parameters

Correctly resolves `TSelf` to `typeparameter TSelf` with containing type information, rather than navigating away.
