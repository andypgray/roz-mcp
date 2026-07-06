---
name: 04-impact-analysis
fixture: nopcommerce
verification:
  - {type: build, expected_exit: 0}
  - {type: file-exists, paths: [IMPACT_ANALYSIS.md]}
  - {type: loc-delta-max, max: 200, ignore_tracked_paths: [IMPACT_ANALYSIS.md]}
---
You're scoping a breaking change to nopCommerce's data layer: widening the return type of the by-id repository lookup `IRepository<TEntity>.GetByIdAsync` (currently `Task<TEntity>`). Before touching any code, enumerate what the change would break across the solution and write a markdown report at `IMPACT_ANALYSIS.md` (in the solution root).

The report must list the impacted sites grouped by project, and tag each as **compatible** (still compiles), **requires-update** (needs a manual edit such as an added cast), or **unsafe** (will not compile, no mechanical fix). For each impacted site, list its project, file, approximate line, verdict, and a one-line reason. Include a per-project summary count of the blast radius.

Do NOT modify any files other than creating `IMPACT_ANALYSIS.md`. Justify the tags with evidence (the consuming expression at each site and why it does or does not survive the new type).
