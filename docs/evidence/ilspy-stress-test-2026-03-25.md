> **Historical record.** Written 2026-03-25 against the tool as it existed then. The server was
> named `roslyn-mcp` before the 2026-07 rename to `roz-mcp`, so the tool names, tool counts, and
> preset sizes in the text below are period-correct for that date, not the current tool surface.
> Published July 2026 verbatim except for the redaction of internal file paths. A few links to
> internal working files (`../backlog/`, run-output `results/`, pre-rename source paths) do not
> resolve in this repository and are left as written rather than rewritten. Bugs identified
> here were subsequently fixed. See the evidence index at [README.md](README.md) for how this
> document maps to the project's current claims.

# Roslyn MCP Server Stress Test — ILSpy (WPF Focus)

**Date:** 2026-03-25
**Evaluator:** Claude Opus 4.6 (1M context)
**Target:** ILSpy open-source decompiler (13 projects, 1,265 files, C# 14 / .NET 10, WPF desktop app)
**Method:** Systematic stress test across 15 categories, ~60 tool calls. Focus on WPF-specific patterns (DependencyProperties, static constructors, attached properties), special symbol names, partial classes, operator-heavy types, and edit tool edge cases. All edits reverted after testing.

---

## Bugs

### 1. `find_overloads` fails for all special symbol names (HIGH)

`find_overloads` returns "No method found" for `.ctor`, `.cctor`, and `op_Implicit`/`op_Explicit`, even when `find_symbol` with the same `symbolName` + `containingType` correctly finds the symbols. `find_callers` also works for these names. Only `find_overloads` fails.

This is high impact because constructors and operators are the primary symbols that *have* overloads.

**Reproduction — `.ctor`:**
```
find_overloads(symbolName: ".ctor", containingType: "ILStructure")
-> "No method found with name '.ctor' in type 'ILStructure'."

# But find_symbol finds 3 constructors:
find_symbol(names: [".ctor"], containingType: "ILStructure")
-> 3 results:
   ILStructure(MetadataFile, MethodDefinitionHandle, MetadataGenericContext, MethodBodyBlock) at ILStructure.cs:91
   ILStructure(MetadataFile, MethodDefinitionHandle, MetadataGenericContext, ILStructureType, int, int, ExceptionRegion) at ILStructure.cs:145
   ILStructure(MetadataFile, MethodDefinitionHandle, MetadataGenericContext, ILStructureType, int, int, int) at ILStructure.cs:157
```

**Reproduction — `op_Implicit`:**
```
find_overloads(symbolName: "op_Implicit", containingType: "JsonValue")
-> "No method found with name 'op_Implicit' in type 'JsonValue'."

# But find_symbol finds 6 implicit operators:
find_symbol(names: ["op_Implicit"], containingType: "JsonValue")
-> 6 results (implicit operators for bool?, double?, string, JsonObject, JsonArray, DateTime?)
```

**Also tested:** `.ctor` on `CSharpResolver` (3 overloads) — same failure.

**Files:** `ICSharpCode.Decompiler/Disassembler/ILStructure.cs`, `ICSharpCode.Decompiler/Metadata/LightJson/JsonValue.cs`, `ICSharpCode.Decompiler/CSharp/Resolver/CSharpResolver.cs`

---

### 2. `find_symbol` for `.cctor` without `containingType` returns 0 results (MEDIUM)

Solution-wide search for static constructors returns nothing. Adding `containingType` finds them correctly.

**Reproduction:**
```
find_symbol(names: [".cctor"], maxResults: 20, excludeTests: true)
-> "No symbols found matching '.cctor'."

# With containingType:
find_symbol(names: [".cctor"], containingType: "SharpTreeView")
-> 1 result: static constructor at SharpTreeView.cs:39

find_symbol(names: [".cctor"], containingType: "CollapsiblePanel")
-> 1 result: static constructor at CollapsiblePanel.cs:34
```

At least 6 static constructors exist in WPF control classes (`SharpTreeView`, `SharpTreeNodeView`, `EditTextBox`, `SharpTreeViewItem`, `CollapsiblePanel`, `ZoomScrollViewer`).

**Files:** `ILSpy/Controls/TreeView/SharpTreeView.cs:39`, `ILSpy/Controls/CollapsiblePanel.cs:34`, `ILSpy/Controls/TreeView/SharpTreeNodeView.cs:32`

---

### 3. `find_symbol` with `kind=Delegate` returns nothing (MEDIUM)

The `Delegate` kind filter matches zero symbols regardless of name or pattern.

**Reproduction:**
```
find_symbol(names: ["*"], kind: "Delegate", excludeTests: true, maxResults: 15)
-> "No symbols found matching '*' with kind 'Delegate'."

find_symbol(names: ["Handler", "Action", "Func", "Predicate"], kind: "Delegate", excludeTests: true)
-> All 4 searches return 0 results.
```

Delegate declarations exist in the solution (e.g., in test case files and `OptionalArguments.cs`).

---

### 4. `add_usings` accepts syntactically invalid namespaces (LOW)

No validation on the namespace string — spaces, invalid characters are accepted.

**Reproduction:**
```
add_usings(filePath: "ILSpy/Commands/SimpleCommand.cs", usings: ["this.is.not.a.valid namespace"])
-> "Added 1 using(s) to ILSpy\Commands\SimpleCommand.cs: this.is.not.a.valid namespace"
```

The using is written to the file verbatim. The user will see compiler errors, but the tool should reject clearly invalid input.

**File:** `ILSpy/Commands/SimpleCommand.cs`

---

### 5. `get_diagnostics` silently ignores nonexistent project names (LOW)

When `project` doesn't match any loaded project, the tool returns "No diagnostics" — identical to the output for a real project with no issues. The caller cannot distinguish "project is clean" from "project doesn't exist".

**Reproduction:**
```
# Step 1: Introduce an error in TestPlugin
replace_content(
  filePath: "TestPlugin/CustomLanguage.cs",
  search: 'return "Custom";',
  replace: "return NonExistentType.Foo();"
)

# Step 2: Query the real project — error found
get_diagnostics(project: "TestPlugin", severity: "Error")
-> "TestPlugin\CustomLanguage.cs:23: error CS0103: The name 'NonExistentType' does not exist..."
-> "Summary: 1 error(s), 0 warning(s), 0 info"

# Step 3: Query a nonexistent project — no warning, looks clean
get_diagnostics(project: "NonExistentProject", severity: "Error")
-> "No diagnostics at severity 'Error' or above in project 'NonExistentProject'."

# Step 4: Query a real clean project — identical output to step 3
get_diagnostics(project: "ILSpy.Tests", severity: "Error")
-> "No diagnostics at severity 'Error' or above in project 'ILSpy.Tests'."
```

Steps 3 and 4 produce indistinguishable output. The `project` parameter is a substring match, so a typo like `"Decompiler.Test"` (missing the `s`) would silently match nothing.

**Compare with:** `get_symbols_overview` on a nonexistent file correctly returns `"Error: File not found in solution"`.

**Suggested fix:** When `project` is provided but matches zero loaded projects, return an error or warning listing available project names (or the closest match).

---

### 6. `rename_symbol` reports error on successful completion (MEDIUM)

A large rename (45 files) completed fully but reported a file-lock error. The error message implies the rename failed when `git diff` shows all 45 files were modified correctly.

**Reproduction:**
```
rename_symbol(
  filePath: "ICSharpCode.Decompiler/Util/EmptyList.cs",
  symbolName: "EmptyList",
  line: 27, column: 22,
  newName: "EmptyCollection",
  renameFile: true
)
-> Error: "The process cannot access the file '...MonoCecilDebugInfoProvider.cs' because it is being used by another process."

# But git shows the rename completed:
git diff --stat -> 45 files changed, 152 insertions(+), 152 deletions(-)

# Even the "locked" file was modified:
git diff -- ICSharpCode.ILSpyX/PdbProvider/MonoCecilDebugInfoProvider.cs -> 3 hunks, all EmptyList→EmptyCollection
```

The error was thrown after all file writes completed (possibly during `renameFile` or workspace reload). The result is confusing — the user might try to "fix" a successful rename.

**Note:** A retry of the rename (without the file lock) also showed odd behavior: `rename_symbol(symbolName: "EmptyList", ...)` said "Symbol 'EmptyList' not found" because the workspace already had the renamed symbol. Using `line`/`column` then returned "Symbol is already named 'EmptyCollection'. No changes made." — so the rename did persist despite the error.

**Files:** `ICSharpCode.Decompiler/Util/EmptyList.cs`, `ICSharpCode.ILSpyX/PdbProvider/MonoCecilDebugInfoProvider.cs`

---

### 7. `remove_unused_usings` strips file-header comments (HIGH)

`remove_unused_usings` deletes copyright/license comment blocks at the top of the file when the **first** `using` directive is removed. The comments are leading trivia on the first using node in the Roslyn syntax tree — removing that node drops the trivia instead of reattaching it to the new first using.

**Root cause:** The bug is in `remove_unused_usings` alone. The initial report blamed `insert_after_symbol` → `replace_symbol` → `remove_symbol`, but those operations were a red herring — they happened to precede `add_usings` + `remove_unused_usings` in the same session.

**Conditions:**
- The removed using must be (or cause a change to) the **first** using directive in the file
- If the removed using is not the first, the header survives
- `remove_unused_usings` on a file with no unused usings is safe (no rewrite occurs)

**Minimal reproduction:**
```
# File: TestPlugin/CustomLanguage.cs
# Lines 1-3: copyright header comments + blank line
# Line 4: using System.Composition;  (first using)

# Step 1: Add an unused using that sorts before the existing first using
add_usings(filePath: "TestPlugin/CustomLanguage.cs", usings: ["System.Collections.Generic"])
# -> "Added 1 using(s)..."
# git diff shows: header intact, +using System.Collections.Generic inserted as new line 4 (sorted before System.Composition)

# Step 2: Remove unused usings
remove_unused_usings(filePaths: ["TestPlugin/CustomLanguage.cs"])
# -> "removed 1 unused using(s): System.Collections.Generic"

# Step 3: Check result
git diff:
-// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
-// This code is distributed under MIT X11 license (for details please see \doc\license.txt)
-
 using System.Composition;
```

**Also reproducible without `add_usings`:**
```
# Insert unused using manually via replace_content (bypassing add_usings entirely)
replace_content(
  filePath: "TestPlugin/CustomLanguage.cs",
  search: "using System.Composition;",
  replace: "using System.Collections.Generic;\nusing System.Composition;"
)
# git diff: header intact, using added

remove_unused_usings(filePaths: ["TestPlugin/CustomLanguage.cs"])
# git diff: header deleted — same bug
```

**Control: removing a non-first using preserves the header:**
```
# Insert unused using AFTER the first using group (between Windows.Controls and ICSharpCode.Decompiler)
replace_content(
  filePath: "TestPlugin/CustomLanguage.cs",
  search: "using System.Windows.Controls;\n\nusing ICSharpCode.Decompiler;",
  replace: "using System.Windows.Controls;\n\nusing System.Collections.Generic;\nusing ICSharpCode.Decompiler;"
)

remove_unused_usings(filePaths: ["TestPlugin/CustomLanguage.cs"])
# git diff: (empty) — header preserved, file identical to original
```

**File:** `TestPlugin/CustomLanguage.cs`

---

## Cosmetic Issues

### 8. Event display duplicates the `event` keyword

`find_symbol` for events shows `event` twice — once as the kind label, once from the declaration.

```
find_symbol(names: ["CanExecuteChanged"], containingType: "SimpleCommand")
-> "public event event EventHandler CanExecuteChanged"
                ^^^^^
```

Should be `public event EventHandler CanExecuteChanged`.

**File:** `ILSpy/Commands/SimpleCommand.cs:28`

---

### 9. Records displayed as `class`/`struct` — the `record` keyword is lost

Record types lose their `record` modifier in display, which is meaningful type information.

```
find_symbol(names: ["PackageCheckResult"], depth: 1)
-> "internal class PackageCheckResult : IEquatable<PackageCheckResult>"
# Should be: "internal record PackageCheckResult : IEquatable<PackageCheckResult>"

find_symbol(names: ["ProjectItemInfo"], depth: 1)
-> "public sealed struct ProjectItemInfo : IEquatable<ProjectItemInfo>"
# Should be: "public sealed record struct ProjectItemInfo : IEquatable<ProjectItemInfo>"
```

**Files:** `ICSharpCode.ILSpyCmd/DotNetToolUpdateChecker.cs:14`, `ICSharpCode.Decompiler/CSharp/ProjectDecompiler/WholeProjectDecompiler.cs:815`

---

### 10. Enums displayed with `sealed` modifier

```
find_symbol(names: ["Modifiers"], kind: "Enum", depth: 1)
-> "public sealed enum Modifiers"
```

While technically accurate, nobody writes `sealed enum` in C#. This is noise.

**File:** `ICSharpCode.Decompiler/CSharp/Syntax/Modifiers.cs:34`

---

## Suggestions

### 11. `find_references` output is token-heavy for high-frequency symbols

`find_references` for `ITextOutput` with `maxResults=100` returned 100 entries at ~2 lines each (~200 lines). Each entry repeats the full file path.

**Suggestion:** Offer a compact/grouped output mode — show the file header once, then just line numbers. Something like:

```
ICSharpCode.Decompiler\Disassembler\DisassemblerHelpers.cs:
  64, 72, 141, 182, 187, 220, 225, 254, 283
ICSharpCode.Decompiler\Disassembler\ReflectionDisassembler.cs:
  40, 87, 92, 320, 487, 491, 766, 812, 1755, 1970, 1987
```

This would be ~5x more token-efficient for high-frequency symbols.

---

### 12. `find_callers` / `find_references` should show test/non-test split in footer

Currently: `(8 total — increase maxResults, or use excludeBaseCalls/excludeTests to narrow)`

**Suggestion:** `(8 total: 5 source, 3 tests — increase maxResults, or use excludeTests to narrow)`

This saves a round-trip when deciding whether `excludeTests` matters.

---

### 13. `find_symbol` with standalone `*` should work as "match all"

`"*Node"` returns 124 results (suffix glob works). `"Decompil*"` returns 7 results (prefix glob works). But `"*"` alone returns nothing.

**Suggestion:** Make `"*"` match all symbols, filtered by the other parameters (`kind`, `project`, `containingType`, etc.). This would enable queries like "find all delegates in the ILSpy project" — currently impossible due to both the `*` issue and the `kind=Delegate` bug (#3).

---

### 14. `replace_content` regex mode: include before/after preview in success message

Currently: `Replaced 1 occurrence(s)... At line(s): 32`

When using capture group substitutions (`$1virtual $3`), there's no way to verify the replacement worked correctly without a separate Read call.

**Suggestion:** Show a 1-2 line diff preview:
```
Replaced 1 occurrence(s) in SimpleCommand.cs at line 32:
- public abstract void Execute
+ public virtual void Execute
```

---

### 15. Document `find_overloads` limitations for special symbol names

The MCP server instructions list `.ctor`, `op_*`, `this[]` etc. as valid symbol names. `find_overloads` appears to accept them in docs but silently fails. Either fix the tool (bug #1) or document the limitation with a workaround:

> `find_overloads` does not support special symbol names (`.ctor`, `.cctor`, `op_*`). Use `find_symbol(names: [".ctor"], containingType: "MyType")` instead.

---

### 16. Document wildcard behavior in `find_symbol` `matchMode`

The `Contains` match mode supports `*` as a glob wildcard within patterns (e.g., `"Decompil*"`, `"*Node"`), but this isn't mentioned in the parameter description. The `matchMode` enum description just says "Match mode" with values `Contains/StartsWith/EndsWith/Exact`. The glob support should be documented since it's genuinely useful — and the edge case where standalone `"*"` returns nothing should be noted.
