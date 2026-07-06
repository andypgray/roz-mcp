---
name: 11-method-interface
fixture: nopcommerce
verification:
  - {type: build, expected_exit: 0}
  - {type: file-exists, paths: [INBOUND.md]}
  - {type: loc-delta-max, max: 200, ignore_tracked_paths: [INBOUND.md]}
---
You're assessing the blast radius of `GetProductByIdAsync` — the by-id catalog lookup declared on the `IProductService` interface (in `Nop.Services.Catalog`) and implemented by `ProductService`. Callers depend on the interface, not the concrete class, so they dispatch through `IProductService`. Without modifying any code, write a markdown report at `INBOUND.md` (in the solution root) that enumerates who calls this method.

The report must document:

(a) **signature** — the method's full signature and return type, with the `file:line` of both the interface declaration and the implementation;
(b) **inbound** — every call site across the solution, grouped by project, with a `file:line` for each. Because callers go through the `IProductService` interface, make sure the enumeration reflects interface-dispatched call sites, not only direct calls on the concrete type. Note any reflection/DI registration of `ProductService` if direct callers are sparse;
(c) a per-project **count** of the inbound call sites, and a closing one-paragraph note on which projects depend most heavily on this lookup.

Cite `file:line` for the declaration and for representative call sites in each project. Do NOT modify any files other than creating `INBOUND.md`. Justify each inbound claim with evidence (the calling expression at each site).
