> **Historical record.** Written 2026-04-06 against the tool as it existed then. The server was
> named `roslyn-mcp` before the 2026-07 rename to `roz-mcp`, so the tool names, tool counts, and
> preset sizes in the text below are period-correct for that date, not the current tool surface.
> Published July 2026 verbatim except for the redaction of internal file paths. A few links to
> internal working files (`../backlog/`, run-output `results/`, pre-rename source paths) do not
> resolve in this repository and are left as written rather than rewritten. Bugs identified
> here were subsequently fixed. See the evidence index at [README.md](README.md) for how this
> document maps to the project's current claims.

# MathNet.Numerics Stress Test Results (2026-04-06)

## Overview

Stress-tested the Roslyn MCP server against [mathnet/mathnet-numerics](https://github.com/mathnet/mathnet-numerics), a pure-managed .NET math library. MathNet was chosen to validate arithmetic operator overloads at scale, extension-method-dominated APIs, multi-TFM deduplication, and complex generic type hierarchies (Matrix<T>, Vector<T>).

**Solution**: `MathNet.Numerics.sln` — 10 logical projects, 33 TFM-expanded, 2,794 documents, multi-TFM (net6.0/net8.0/netstandard2.0)

**Method**: ~62 tool calls across 10 categories, covering 18 of 19 Roslyn MCP tools (all except `remove_unused_usings`). All edits reverted via `git checkout` after testing.

**Result**: 5 bugs identified (1 CRITICAL, 2 MEDIUM, 2 LOW).

---

## Bugs

### BUG-1 (MEDIUM): `find_overloads` requires `containingType` for operators even when `filePath` is provided

#### Summary

`find_overloads` with a `filePath` that unambiguously scopes to a single type still errors for operator symbol names, demanding `containingType`. Non-operator symbols work with `filePath` alone. This forces callers to know the containing type name before they can query overloads — defeating the purpose of `filePath` scoping.

#### Reproduction Steps

1. Confirm `Matrix.Operators.cs` contains operators for a single type (`Matrix<T>`):

   ```json
   { "tool": "get_symbols_overview", "filePaths": ["src/Numerics/LinearAlgebra/Matrix.Operators.cs"] }
   ```

   Result: One type (`Matrix<T>`) with 18 operators.

2. Run `find_overloads` with `filePath` only:

   ```json
   {
     "symbolName": "op_Multiply",
     "filePath": "src/Numerics/LinearAlgebra/Matrix.Operators.cs"
   }
   ```

3. **Expected**: 5 overloads of `operator *` in `Matrix<T>` (Matrix×Matrix, Matrix×scalar, scalar×Matrix, Matrix×Vector, Vector×Matrix).

4. **Actual**: Error: `containingType is required when symbolName is 'op_Multiply'. Specify the type that contains the member.`

5. Same error for `op_Subtraction` with `filePath: "src/Numerics/LinearAlgebra/Vector.Operators.cs"`.

6. **Workaround**: Adding `containingType: "Matrix"` succeeds and returns all 5 overloads.

#### Impact

Callers using `filePath`-based scoping (the natural approach when editing a file) must also know the containing type name for operators. This is inconsistent with non-operator symbols where `filePath` alone is sufficient.

---

### BUG-2 (LOW): `find_implementations` on static operator gives misleading disambiguation error instead of explaining operators have no implementations

#### Summary

`find_implementations` on a static operator returns an "Ambiguous: N overloads" error rather than a message explaining that operators are static and cannot be overridden or implemented. The error implies the query would succeed with disambiguation, but operators can never have implementations.

#### Reproduction Steps

1. Confirm `Complex32` has 3 overloads of `op_Addition`:

   ```json
   { "tool": "find_overloads", "symbolName": "op_Addition", "containingType": "Complex32" }
   ```

   Result: 3 overloads (Complex32+Complex32, Complex32+float, float+Complex32).

2. Run `find_implementations`:

   ```json
   {
     "symbolName": "op_Addition",
     "containingType": "Complex32"
   }
   ```

3. **Expected**: A message like "Operators are static and have no implementations. Use `find_callers` to find call sites."

4. **Actual**: Error: `Ambiguous: 3 overloads of 'op_Addition' in 'Complex32'. Drop symbolName and use filePath+line+column to select a specific overload:`
   — followed by all 3 overload signatures.

5. Disambiguating by position (e.g. `filePath: "src/Numerics/Complex32.cs", line: 557, column: 42`) still returns "No implementations found" — confirming the disambiguation was a red herring.

#### Impact

Low — agents will waste a tool call disambiguating before getting the real answer ("no implementations"). The error should short-circuit with an explanation that operators are always static.

---

### BUG-3 (MEDIUM): TFM duplication in `find_overloads` disambiguation error inflates overload count

#### Summary

When `find_overloads` encounters multiple types with the same name (e.g. `MatrixBuilder` in 4 namespaces), the disambiguation error lists each overload once per TFM, artificially inflating the count. The same method at the same file:line appears 3-4 times.

#### Reproduction Steps

1. Confirm the solution has 3 TFMs for the Numerics project:

   ```json
   { "tool": "get_workspace_info", "project": "Numerics" }
   ```

   Result: `Numerics [classlib] (C# 7.3, net6.0 | net8.0 | netstandard2.0, ...)`

2. Run `find_overloads`:

   ```json
   {
     "symbolName": "Dense",
     "containingType": "MatrixBuilder"
   }
   ```

3. **Expected**: An ambiguity error listing each unique overload once across the 4 namespace-scoped `MatrixBuilder` types (~9 unique methods).

4. **Actual**: Error: `Ambiguous: 36 overloads of 'Dense' in 'MatrixBuilder'.` The list shows:

   ```
   Double.MatrixBuilder.Dense(DenseColumnMajorMatrixStorage<double>) — Builder.cs:46
   Double.MatrixBuilder.Dense(DenseColumnMajorMatrixStorage<double>) — Builder.cs:46
   Double.MatrixBuilder.Dense(DenseColumnMajorMatrixStorage<double>) — Builder.cs:46
   Double.MatrixBuilder.Dense(DenseColumnMajorMatrixStorage<double>) — Builder.cs:46
   Single.MatrixBuilder.Dense(DenseColumnMajorMatrixStorage<float>) — Builder.cs:114
   Single.MatrixBuilder.Dense(DenseColumnMajorMatrixStorage<float>) — Builder.cs:114
   Single.MatrixBuilder.Dense(DenseColumnMajorMatrixStorage<float>) — Builder.cs:114
   Single.MatrixBuilder.Dense(DenseColumnMajorMatrixStorage<float>) — Builder.cs:114
   ... and 26 more
   ```

   Each `Double.MatrixBuilder.Dense(...)` at line 46 appears **4 times** — the 3 TFMs plus one extra. Same for Single, Complex, Complex32 variants.

5. The inflated count of 36 should be ~9 unique overloads (across the 4 namespace-scoped MatrixBuilder types with ~2-3 factory overloads each at the concrete level, plus the abstract base).

#### Impact

The inflated count makes disambiguation harder for agents. When the error says "36 overloads", the agent may infer a highly complex API, but the actual unique count is ~4× lower. Other read-only tools (e.g. `find_symbol`, `find_derived_types`) deduplicate TFMs correctly — this bug is specific to the disambiguation error path in `find_overloads`.

#### Notes

This is the only TFM deduplication issue found. All other read-only tools handled multi-TFM correctly — `find_symbol` returns each type once, `find_derived_types` shows deduplicated trees, `get_workspace_info` lists TFMs pipe-separated per project.

---

### BUG-4 (MEDIUM): `find_implementations` returns empty for non-abstract override chain (`Matrix<T>.At`)

#### Summary

`find_implementations` returns "No implementations found" for `Matrix<T>.At(int, int)`, a public method overridden by DenseMatrix, SparseMatrix, and DiagonalMatrix across 4 numeric-type namespaces (12+ overrides expected). The method is non-abstract at the `Matrix<T>` level (it delegates to `Storage.At()`), but concrete subclasses override it.

#### Reproduction Steps

1. Confirm `At` exists as a public method on `Matrix<T>`:

   ```json
   { "tool": "get_symbols_overview", "filePaths": ["src/Numerics/LinearAlgebra/Matrix.cs"], "maxMembers": 10 }
   ```

   Result shows `At(int row, int column)` at line 116.

2. Read the source to confirm the method signature:

   ```csharp
   // src/Numerics/LinearAlgebra/Matrix.cs:116
   [MethodImpl(MethodImplOptions.AggressiveInlining)]
   [TargetedPatchingOptOut("Performance critical to inline across NGen image boundaries")]
   public T At(int row, int column)
   {
       return Storage.At(row, column);
   }
   ```

3. Run `find_implementations` by position:

   ```json
   {
     "symbolName": "At",
     "filePath": "src/Numerics/LinearAlgebra/Matrix.cs",
     "line": 116,
     "column": 18
   }
   ```

4. **Expected**: 12+ implementations across DenseMatrix, SparseMatrix, DiagonalMatrix in Double, Single, Complex, Complex32 namespaces.

5. **Actual**: `No implementations found for 'At'.`

6. The method is not marked `virtual` or `abstract` at the `Matrix<T>` level — it's a concrete method that delegates to `Storage.At()`. This explains why `find_implementations` returns nothing: the method is not virtual, so there are no overrides in the C# sense.

#### Impact

This is arguably correct behavior — the method isn't virtual so there are no overrides. However, the concrete subclasses may define their own `At` methods (via `new` hiding) that the user would want to find. An agent asking "what overrides At?" gets a misleading empty result when the real answer is "At is not virtual — subclass specialization happens at the Storage level."

#### Notes

Running `find_implementations` by name without a position (`symbolName: "At", containingType: "Matrix"`) gives an "Ambiguous: 2 overloads" error (getter vs setter `At`). The getter overload is the non-virtual method. Checking `DenseMatrix` source confirms it overrides `Storage.At()` via its storage implementation, not by overriding the `Matrix<T>.At()` method directly.

---

### BUG-5 (CRITICAL): `rename_symbol` ignores `symbolName` parameter when `line`+`column` are provided, enabling catastrophic type renames

#### Summary

`rename_symbol` silently ignores the `symbolName` parameter when `line` and `column` are also provided, resolving the symbol purely by position. If the cursor lands on a type token instead of the intended identifier, the tool renames the type across the entire solution with no safeguard or confirmation. In this reproduction, attempting to rename a parameter named `summand1` instead renamed the `Complex32` struct across 139 files.

#### Reproduction Steps

1. Confirm the target line in `Complex32.cs`:

   ```
   // src/Numerics/Complex32.cs:557
   //         1111111111222222222233333333334444444444555555555566
   //1234567890123456789012345678901234567890123456789012345678901
   public static Complex32 operator +(Complex32 summand1, Complex32 summand2)
   ```

   Column 46 lands on the `l` in the second `Complex32` (the parameter's type annotation). Column 54 would be the start of `summand1` (the parameter name).

2. Run `rename_symbol` intending to rename the parameter `summand1`:

   ```json
   {
     "filePath": "src/Numerics/Complex32.cs",
     "symbolName": "summand1",
     "line": 557,
     "column": 46,
     "newName": "leftOperand"
   }
   ```

3. **Expected**: Either (a) rename `summand1` to `leftOperand` in the operator signature and body, or (b) error: "Symbol at position is 'Complex32', not 'summand1'."

4. **Actual**: `Renamed 'Complex32' to 'leftOperand'. Changed 139 file(s):` — followed by a list of 139 files across 8 projects. The `Complex32` struct, all its references, type annotations, and usages across the entire solution are renamed to `leftOperand`.

5. The `symbolName: "summand1"` parameter was completely ignored. The tool resolved the symbol at position (557, 46) — which is the `Complex32` type token — and renamed that.

6. Revert: `git checkout -- src/` restores all 139 files.

#### Sub-bugs

This is actually two bugs:

1. **`symbolName` is ignored when `line`+`column` are present**: The tool should either validate that the resolved symbol matches `symbolName`, or use `symbolName` to find the correct token on the line. Currently, position resolution silently overrides the name parameter.

2. **No blast-radius safeguard**: Renaming a type with 3,913 references across 139 files happens silently. There should be a warning or confirmation when a rename affects more than a threshold number of files (e.g. >10).

#### Impact

**Critical** — an off-by-one column error silently renames a core type across the entire solution. This is especially dangerous for agents that calculate column positions programmatically (as they must for operator declarations where the symbol token is surrounded by keywords and punctuation).

In this case the column was off by 8 positions (46 instead of 54), which is an easy mistake when counting columns in a line with 8 leading spaces + multiple keywords.

#### Suggested Fix

Option A (validate): When both `symbolName` and `line`+`column` are provided, verify the resolved symbol's name matches `symbolName`. If not, error: "Symbol at (557, 46) is 'Complex32', not 'summand1'. Did you mean column 54?"

Option B (blast-radius guard): Before applying a rename that affects >N files (e.g. 10), return a preview: "This will rename 'Complex32' across 139 files. Provide `confirm: true` to proceed."

Option C (both): Validate name match AND add blast-radius guard.

---

## Cosmetic Issues

### COS-1: `find_symbol` base type rendering omits generic type parameter in summary

When `find_symbol` returns `DenseMatrix` (in the `Double` namespace), the base type is shown as `class DenseMatrix : Matrix` rather than `class DenseMatrix : Matrix<double>`. Technically correct — there is an intermediate non-generic `Double.Matrix` class — but displaying the resolved generic base would be more informative for users who think in terms of the generic hierarchy.

**Reproduction**:

```json
{ "tool": "find_symbol", "names": ["DenseMatrix"], "kind": "Class",
  "filePaths": ["src/Numerics/LinearAlgebra/Double/DenseMatrix.cs"] }
```

**Result**: `public class DenseMatrix : Matrix` — no `<double>` in the base type.

**Compare with `get_type_hierarchy`** on the same type, which correctly shows:

```
Base types:
  Matrix (Double\Matrix.cs:41)
    Matrix<double> (Matrix.Arithmetic.cs:38)
```

The hierarchy tool resolves the full generic chain; `find_symbol` stops at the immediate base.

---

### COS-2: `get_symbols_overview` doesn't expand nested private classes at depth=1

For `AmosHelper.cs` (7,676 lines — the largest file in the solution), `get_symbols_overview` at the default depth=1 shows only the parent type with the nested class name, not its members:

```json
{ "tool": "get_symbols_overview", "filePaths": ["src/Numerics/SpecialFunctions/Amos/AmosHelper.cs"] }
```

**Result**:

```
public static partial class SpecialFunctions  :5
  Namespace: MathNet.Numerics
  Members (1):
    [private static class] AmosHelper
```

The entire file's content (7,676 lines of helper methods) is hidden behind a single nested class entry. The user must explicitly request `depth: 2` to see `AmosHelper`'s members. For files with a single dominant nested type, auto-expanding or prompting "1 nested type with N members — use depth: 2 to expand" would be more useful.

---

### COS-3: `insert_symbol` with `line` but no `column` errors on leading whitespace

When `insert_symbol` is called with `line: 557` but no `column`, it defaults to column 1, which hits leading whitespace in indented code:

```json
{
  "tool": "insert_symbol",
  "filePath": "src/Numerics/Complex32.cs",
  "symbolName": "op_Addition",
  "line": 557,
  "content": "..."
}
```

**Result**: `No symbol found at exact position src/Numerics/Complex32.cs:557:1 — on whitespace. Cursor must be on the symbol's identifier token.`

**Workaround**: Add `column: 42` (the `+` operator token position). This is documented behavior for edit tools ("strict — cursor must be on the identifier token"), but for line-only calls the tool could scan the line for the named symbol rather than defaulting to column 1.

---

## Suggestions

### SUG-1: `find_symbol` with `containingType` should support open generic syntax for member lookup

`find_symbol` for `Build` (a static field on `Matrix<T>`) with `containingType: "Matrix<>"` returns nothing:

```json
{ "tool": "find_symbol", "names": ["Build"], "containingType": "Matrix<>", "kind": "Property" }
```

**Result**: `No symbols found matching "Build" with kind 'Property' in type 'Matrix<>'.`

Two issues compound here:

1. `Build` is declared as a **static field**, not a property (discovered via `get_symbols_overview`). Searching with `kind: "Property"` misses it — but a user looking for `Matrix<T>.Build` would naturally guess "Property" from usage patterns like `Matrix<double>.Build.Dense(...)`.

2. Even with `kind` omitted, `containingType: "Matrix<>"` doesn't match. The open generic syntax works for top-level `find_symbol` searches (e.g. `names: ["Matrix<>"]`) but not for `containingType` member filtering.

**Workaround**: Use `get_symbols_overview` on the file instead, which correctly shows `[public static field] MatrixBuilder<T> Build`.

---

### SUG-2: Document the `find_callers` vs `find_references` count disparity for operators

For `op_Addition` on `Complex32`:
- `find_callers` returned **74** results
- `find_references` returned **184** results

The 2.5× difference is because `find_references` includes every individual occurrence within a method (each `sum += x` line), while `find_callers` groups by calling method. This distinction is useful but not obvious from the tool descriptions, which both say they "find" references/callers.

A note in the `find_references` description like "Returns per-location results (multiple per method). For per-method grouping, use `find_callers`" would help agents choose the right tool.

---

## What Works Well

### Multi-TFM Deduplication

The standout finding — nearly flawless across all read-only tools.

| Tool | Test | Result |
|------|------|--------|
| `get_workspace_info` | 10 projects, 33 TFM-expanded | Projects listed once with `net6.0 \| net8.0 \| netstandard2.0` |
| `find_symbol("Complex32")` | Struct across 3 TFMs | Exactly 1 result |
| `find_symbol("DenseMatrix")` | Class across 3 TFMs | Exactly 1 result |
| `find_derived_types("Matrix<>")` | 16 derived types | Each listed once, tree format |
| `find_callers("Mean")` | Extension method | 25 callers, not 75 (3×) |
| `get_type_hierarchy("DenseMatrix")` | Inheritance chain | Not duplicated |

### Operator Overload Navigation

| Query | Results |
|-------|---------|
| `find_overloads("op_Addition", containingType: "Complex32")` | 3 overloads with clear param types |
| `find_overloads("op_Multiply", containingType: "Matrix")` | 5 overloads — generic `T`, `Matrix<T>`, `Vector<T>` rendered |
| `find_overloads("op_Division", containingType: "Complex32")` | 3 overloads |
| `find_symbol("op_Addition", kind: Operator)` | 30 operators across all types, grouped by containing type |
| `find_callers("op_Addition", containingType: "Complex32")` | 74 callers — traces `a + b` syntax and `+=` compound assignments |
| `find_references("op_Addition", containingType: "Complex32")` | 184 references across 4 projects |
| `get_symbols_overview(Complex32.cs, memberKinds: [Operator])` | 29 operators with readable signatures |

The `get_symbols_overview` output for operators is particularly good — it distinguishes arithmetic (`operator +`), comparison (`operator ==`), and conversion (`explicit operator Complex32`, `implicit operator Complex32`) operators with full signatures.

### Conversion Operator Separation

| Query | Results |
|-------|---------|
| `find_overloads("op_Implicit", containingType: "Complex32")` | 10 implicit conversions only |
| `find_symbol("op_Implicit", containingType: "Complex32")` | 10 results — only implicit, no arithmetic |
| `find_symbol("op_Explicit", containingType: "Complex32")` | 3 results — only explicit (decimal, Complex, double) |
| `find_callers("op_Implicit", containingType: "Complex32")` | 15 callers across 4 projects — traces casts like `(Complex32)3.0f` |
| `find_references("op_Implicit", containingType: "Complex32")` | 27 references across 4 projects |

Clean separation — `op_Implicit` never returns arithmetic operators and vice versa. The `find_overloads` format for conversions (`-> Complex32 (byte value)`) is distinct from arithmetic operators and very readable.

### Extension Method Tracing

| Query | Results |
|-------|---------|
| `find_callers("Mean")` | 25 callers — both `data.Mean()` extension syntax and `Statistics.Mean(data)` static calls |
| `find_overloads("Mean")` | 3 overloads — `this IEnumerable<double>`, `this IEnumerable<float>`, `this IEnumerable<double?>` |
| `find_symbol("Mean", kind: Method)` | 55 results across ArrayStatistics, Statistics, StreamingStatistics |
| `go_to_definition` on `inputData.Mean()` | Navigates to `Statistics.cs:229` — correct static method declaration |
| `find_references("Statistics", kind: Class)` | 134 refs — static call syntax only (extension calls not attributed to class) |

`go_to_definition` from extension syntax (`inputData.Mean()` in test code) correctly navigates to the static extension method declaration. The `this` keyword is shown in overload signatures, identifying extension methods.

### Type Hierarchy and Distribution Types

| Query | Results |
|-------|---------|
| `find_derived_types("Matrix<>")` | 16 types in tree format across 4 namespaces |
| `find_derived_types("IContinuousDistribution")` | 26 distributions (Beta through Exponential) |
| `find_derived_types("IDiscreteDistribution")` | 11 distributions (Bernoulli through Poisson) |
| `find_implementations("Sample", containingType: "IContinuousDistribution")` | 75 implementations (instance + static overloads) |
| `get_type_hierarchy("Normal")` | IContinuousDistribution → IUnivariateDistribution → IDistribution |
| `get_type_hierarchy("DenseMatrix")` | DenseMatrix → Matrix → Matrix<double> |

The `find_derived_types` tree format for `Matrix<>` is excellent:

```
Matrix (Double\Matrix.cs:41)
├─ DenseMatrix (Double\DenseMatrix.cs:48)
├─ DiagonalMatrix (Double\DiagonalMatrix.cs:52)
└─ SparseMatrix (Double\SparseMatrix.cs:46)
Matrix (Complex32\Matrix.cs:43)
├─ DenseMatrix (Complex32\DenseMatrix.cs:50)
├─ DiagonalMatrix (Complex32\DiagonalMatrix.cs:54)
└─ SparseMatrix (Complex32\SparseMatrix.cs:48)
```

### Cross-Project Reference Tracking

| Query | Results |
|-------|---------|
| `find_references("Complex32")` | 3,913 refs across 139 files, 8 projects |
| `find_implementations("ILinearAlgebraProvider")` | 4 implementations across 4 projects (Managed, OpenBLAS, MKL, CUDA) |
| `find_derived_types("ILinearAlgebraProvider")` | Same 4 types — consistent with `find_implementations` |
| `find_callers("op_Implicit", containingType: "Complex32")` | 15 callers including Data.Matlab (cross-project) |

The `find_implementations` result for `ILinearAlgebraProvider` is particularly informative — each implementation shows 5 partial class file locations.

### Generic Type Rendering

- Generic constraints displayed: `where T : struct, IEquatable<T>, IFormattable`
- Resolved generic base types: `Matrix<double>` in type hierarchy, not raw `Matrix`
- Tuple return types: `(double Mean, double StandardDeviation)` in `find_symbol` results
- Partial class aggregation: `Matrix<T>` shows 5 file locations with 397 aggregated members

### Wildcard Symbol Search

| Query | Results |
|-------|---------|
| `find_symbol("*Matrix*", kind: Class)` | 37 types across 4 projects |
| `find_symbol("*Distribution*")` | 80 symbols (interfaces, methods, properties, namespace) |

### Edit Operations

| Operation | Target | Result |
|-----------|--------|--------|
| `insert_symbol` | New operator after `op_Addition` (Complex32) | 7 lines inserted, correct indentation |
| `remove_symbol` | The inserted `op_Modulus` | 6 lines removed cleanly |
| `replace_symbol` | `op_Addition` body (Complex32) | 4 → 7 lines, doc comments preserved |
| `insert_symbol` | New `IsSquare()` method on `Matrix<T>` | 7 lines, generic type context preserved |
| `add_usings` | `System.Diagnostics.CodeAnalysis` to Complex32.cs | Added and sorted correctly |
| `get_diagnostics(incremental: true)` | After all edits | "0 new, 0 resolved, 0 unchanged" — no regressions |

All edit operations required `line`+`column` for operator-heavy types (line alone defaults to column 1 which hits whitespace). Indentation was correct in all cases.

### Pagination and Truncation

`find_derived_types("IContinuousDistribution", maxResults: 5)` correctly returns 5 results with "(26 total — increase maxResults)". Consistent truncation behavior across all tools that support `maxResults`.

---

## Tool Coverage

| Tool | Calls | Issues |
|------|-------|--------|
| `find_overloads` | 8 | filePath ignored for operators (BUG-1); TFM duplication in disambiguation (BUG-3) |
| `find_symbol` | 10 | All passed |
| `find_callers` | 5 | All passed |
| `find_references` | 5 | All passed |
| `find_implementations` | 4 | Misleading error for operators (BUG-2); empty for non-virtual At (BUG-4) |
| `find_derived_types` | 5 | All passed |
| `get_type_hierarchy` | 3 | All passed |
| `get_symbols_overview` | 4 | All passed |
| `go_to_definition` | 1 | Passed — extension syntax resolved |
| `get_workspace_info` | 1 | Passed |
| `get_diagnostics` | 2 | All passed |
| `reset_baseline` | 1 | Passed |
| `reload_workspace` | 3 | 1 failure (SDK resolution after global.json revert — user error, not a bug) |
| `insert_symbol` | 2 | 1 retry (whitespace at column 1) |
| `remove_symbol` | 1 | Passed |
| `rename_symbol` | 1 | Catastrophic type rename (BUG-5) |
| `replace_symbol` | 1 | Passed |
| `add_usings` | 1 | Passed |

---

## Test Environment

- **MathNet.Numerics version**: 6.0.0-beta2
- **Solution**: `MathNet.Numerics.sln` — 10 projects, 2,794 documents
- **Target frameworks**: net6.0, net8.0, netstandard2.0 (multi-TFM)
- **Baseline diagnostics**: 0 (clean workspace)
- **Platform**: Windows 10 Pro, .NET SDK 9.0.310 / 10.0.201
