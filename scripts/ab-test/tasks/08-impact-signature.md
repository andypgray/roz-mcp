---
name: 08-impact-signature
fixture: nopcommerce
verification:
  - {type: build, expected_exit: 0}
  - {type: file-exists, paths: [SIGNATURE_IMPACT.md]}
  - {type: loc-delta-max, max: 200, ignore_tracked_paths: [SIGNATURE_IMPACT.md]}
---
You're scoping a signature change to nopCommerce's `IProductService.GetProductByIdAsync` (declared in `Nop.Services`): adding a new required parameter (for example, a `bool includeDeleted` flag). Before touching any code, enumerate every call site that would need a manual update and write a markdown report at `SIGNATURE_IMPACT.md` (in the solution root).

Adding a required parameter breaks every existing call: each call site is **requires-update** — the developer must pass the new argument by hand. The report must:

- List each call site grouped by project, with file, approximate line, and the current call expression.
- Include a per-project summary count of the call sites that need updating, and a grand total.

Do NOT modify any files other than creating `SIGNATURE_IMPACT.md`. Focus on completeness — every call site must be accounted for, since the value here is exhaustive enumeration rather than per-site classification (every site has the same verdict).
