---
name: 06-impact-remove
fixture: nopcommerce
verification:
  - {type: build, expected_exit: 0}
  - {type: file-exists, paths: [REMOVE_IMPACT.md]}
  - {type: loc-delta-max, max: 200, ignore_tracked_paths: [REMOVE_IMPACT.md]}
---
You're planning to DELETE the cache-invalidation member `RemoveByPrefixAsync` from nopCommerce's `IStaticCacheManager` interface (declared in `Nop.Core`). Before touching any code, enumerate everything that would break across the solution and write a markdown report at `REMOVE_IMPACT.md` (in the solution root).

Removing a member is not a mechanical refactor: **every reference site is unsafe** — it will no longer compile and there is no automatic fix, so each caller must be rewritten or removed by hand. The report must:

- List every impacted site grouped by project, with file, approximate line, and a one-line note on what each caller does with `RemoveByPrefixAsync`.
- Call out any overrides or interface implementations that would be **orphaned** by the removal (the concrete cache managers that implement this member).
- Include a per-project summary count of the blast radius and a grand total.

Do NOT modify any files other than creating `REMOVE_IMPACT.md`. Do not propose a replacement API — the task is purely to map what breaks. Be honest that there is no "requires-update" middle ground here: a deleted member breaks every caller outright.
