---
name: 02-audit
fixture: nopcommerce
rubric: audit
report: AUDIT_REPORT.md
verification:
  - {type: build, expected_exit: 0}
  - {type: file-exists, paths: [AUDIT_REPORT.md]}
  - {type: loc-delta-max, max: 200, ignore_tracked_paths: [AUDIT_REPORT.md]}
---
You're reviewing nopCommerce ahead of an upgrade. Produce a markdown report at `AUDIT_REPORT.md` (in the solution root) identifying:

(a) the 5 services with the highest fan-in (most callers across the solution),
(b) the 3 entities referenced from the most projects,
(c) the 3 biggest god-classes by LOC and what concerns they mix.

Do NOT modify any files other than creating `AUDIT_REPORT.md`. Justify each ranking with evidence (e.g., caller counts, project counts, LOC numbers).
