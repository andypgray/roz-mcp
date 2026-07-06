---
name: 13-method-trap
fixture: nopcommerce
verification:
  - {type: build, expected_exit: 0}
  - {type: file-exists, paths: [LEAF.md]}
  - {type: loc-delta-max, max: 200, ignore_tracked_paths: [LEAF.md]}
---
You need a precise definition note for a single method: `HasAnyRecurringProductAsync` on `ProductService` (in `Nop.Services.Catalog`). Without modifying any code, write a short markdown note at `LEAF.md` (in the solution root) describing just this one method from its own declaration.

Document:

(a) its full signature, parameter, and return type, with its `file:line`;
(b) what its body actually does — the exact predicate it evaluates against the product set;
(c) the one-sentence intent from its XML doc comment.

This is a single-symbol definition lookup — the note is about the method itself, not its place in any larger call graph. Cite the `file:line` of the declaration. Do NOT modify any files other than creating `LEAF.md`.
