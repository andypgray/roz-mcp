---
name: 03-refactor-rename
fixture: nopcommerce
verification:
  - {type: build, expected_exit: 0}
  - {type: token-residual, token: IAffiliateService, max_count: 0}
  - {type: token-residual, token: IAffiliateManagementService, min_count: 22}
  - {type: diff-hygiene, max_churn_ratio: 0.5, forbid_bom_strip: true}
---
`IAffiliateService` in nopCommerce has a generic name that obscures its scope. The interface and its one implementation cover affiliate CRUD, friendly-URL validation, and display-name generation — i.e., the management surface for affiliate records. Rename it to `IAffiliateManagementService` across the entire solution.

Requirements:
- Rename the interface type `IAffiliateService` to `IAffiliateManagementService` at every reference: declarations, inheritance lists, injected fields/parameters, DI registrations, tests, plugins, everywhere.
- Rename the containing file from `IAffiliateService.cs` to `IAffiliateManagementService.cs`.
- Do NOT rename the implementation class (`AffiliateService`) — only the interface.
- Do NOT rename the `Affiliate` entity, any controller, model factory, or unrelated type. Only the interface.

Done when:
1. `dotnet build src/NopCommerce.sln` exits 0.
2. A solution-wide search for `IAffiliateService` (case-sensitive, word-boundary) returns zero hits.
3. A solution-wide search for `IAffiliateManagementService` returns the same count of hits as `IAffiliateService` had previously (22).
