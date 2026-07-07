---
name: 05-explain-service
fixture: nopcommerce
verification:
  - {type: build, expected_exit: 0}
  - {type: file-exists, paths: [SERVICE_EXPLAINED.md]}
  - {type: loc-delta-max, max: 200, ignore_tracked_paths: [SERVICE_EXPLAINED.md]}
---
You're onboarding onto nopCommerce and need to understand `ProductService` (the catalog service in `Nop.Services.Catalog`, implementing `IProductService`). Without modifying any code, write a markdown report at `SERVICE_EXPLAINED.md` (in the solution root) that explains the service.

For each of its 8 most important public methods, document:

(a) **purpose** — what the method does, in one sentence;
(b) **inbound** — who calls it, grouped by project (note when a method has no direct callers and is reached only via the `IProductService` interface or DI);
(c) **outbound** — the in-solution collaborators it calls out to (other services, repositories, event publishers), ignoring BCL/LINQ noise.

Cite `file:line` for every method and for representative call sites. Close with a one-paragraph summary of the service's responsibilities and the main collaborators it depends on.

Do NOT modify any files other than creating `SERVICE_EXPLAINED.md`. Justify each inbound/outbound claim with evidence (caller locations, the called expression at each site).
