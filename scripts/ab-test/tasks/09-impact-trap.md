---
name: 09-impact-trap
fixture: nopcommerce
verification:
  - {type: build, expected_exit: 0}
  - {type: file-exists, paths: [RENAME_PLAN.md]}
  - {type: loc-delta-max, max: 200, ignore_tracked_paths: [RENAME_PLAN.md]}
---
You're planning a pure mechanical rename of the interface `IAffiliateService` to `IAffiliateManagementService` across nopCommerce. This is a rename only — the type's members, signatures, accessibility, and behavior are all unchanged. Before touching any code, write a short plan at `RENAME_PLAN.md` (in the solution root).

A rename does not change whether any reference compiles — every site simply switches to the new name and keeps working. So do NOT classify sites by compile-compatibility: there is nothing to classify, because a rename has no "compatible / requires-update / unsafe" split. Instead, the plan should:

- List every reference to `IAffiliateService` grouped by project and file (the interface declaration, the file to rename, inheritance lists, injected fields/parameters, DI registrations, tests, plugins).
- Give the concrete rename steps in order: rename the type, rename the file `IAffiliateService.cs` to `IAffiliateManagementService.cs`, and update all references. Note that the implementation class `AffiliateService` is NOT renamed — only the interface.

Do NOT modify any files other than creating `RENAME_PLAN.md`.
