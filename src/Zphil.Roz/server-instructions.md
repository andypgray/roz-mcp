# Roslyn C# Code Navigation

## Location format
Cursor: `"path"`, `"path:line"`, or `"path:line:col"`.
Name: `symbolNames=["A","B"]`, exclusive with cursor; scope with `project=`.

- `find_references`/`find_implementations`/`find_overloads`/`get_type_hierarchy`/`analyze_change_impact`/`analyze_method`:
  `locations[]`/`symbolNames[]`, **batched** — one call beats N (split when
  `kind`/`project`/`includeTests` differ).
- `go_to_definition`: single `location`; col optional (line-only → that member).
- `edit_symbol`/`change_signature`: single `location`; `symbolName`
  (+`containingType`/`kind`) authoritative; `:line:col` only tie-breaks overloads.
- `rename_symbol`: single `location`, path-only or full `path:line:col` (bare
  `path:line` rejected), cross-checked vs `symbolName`.
- `apply_code_fix`: no location; one `diagnosticId`; flavors ⇒ `equivalenceKey`.

Read tools snap to nearest decl. `go_to_definition` on `override` → base.

## Guides (MCP resources)
`roz://guides/editing` before mutating: verify modes, change_signature
blockers, apply_code_fix equivalenceKey, special names (`.ctor`, `this[]`).
`roz://guides/configuration`: env vars, `ROZ_TOOLS`, .roz.json.

## Shared Parameter Values
- `kind` / `memberKinds`: Class|Interface|Struct|Enum|Delegate|Method|Constructor|Property|Field|Event|Namespace|Indexer|Operator|Destructor
- `referenceKinds` (find_references): All|Invocations|Reads|Writes
- `changeKind` (analyze_change_impact): SignatureChange|RemoveSymbol|TypeChange|AccessibilityNarrow
- `newSignature` (SignatureChange / change_signature): param list e.g. `(string name, int count = 5)`; per-arg verdicts (omit = coarse); one method/call.
- `newAccessibility`: Public|ProtectedInternal|Internal|Protected|PrivateProtected|Private
- `matchMode`: Contains|StartsWith|EndsWith|Exact (case-sensitive)
- `severity`: Hidden|Info|Warning|Error
- `symbolNames[]`: simple name or FQN; `Processor<>` for arity.
- `verify` (all 5 mutating tools): None|Delta|DryRun. Delta commits + reports errors; DryRun previews.
