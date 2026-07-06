---
name: 07-impact-accessibility
fixture: nopcommerce
verification:
  - {type: build, expected_exit: 0}
  - {type: file-exists, paths: [ACCESSIBILITY_IMPACT.md]}
  - {type: loc-delta-max, max: 200, ignore_tracked_paths: [ACCESSIBILITY_IMPACT.md]}
---
You're considering tightening the accessibility of `GetLocalizedAsync` on nopCommerce's `ILocalizationService` (declared in `Nop.Services`) from `public` to `internal`. This is the generic entity-property localizer (`GetLocalizedAsync<TEntity, TPropType>`) called throughout the solution to fetch a localized value for an entity. Before touching any code, work out which references survive the change and which break, then write a markdown report at `ACCESSIBILITY_IMPACT.md` (in the solution root).

In C#, an `internal` member is only accessible from within the same assembly. So the split is binary:

- References in the SAME assembly as the declaration (`Nop.Services`) stay **compatible** (still compile).
- References in a DIFFERENT assembly (`Nop.Web`, `Nop.Web.Framework`, plugins, tests) become **unsafe** (will no longer compile). There is no "requires-update" verdict for an accessibility change — a site either still has access or it does not.

The report must separate same-assembly (compatible) sites from cross-assembly (unsafe) sites, list each impacted site with its project, file, approximate line, and verdict, and give a per-project summary count plus a grand total of the compatible-vs-unsafe split.

Do NOT modify any files other than creating `ACCESSIBILITY_IMPACT.md`. The discriminating judgment is the assembly boundary — get the compatible/unsafe partition right.
