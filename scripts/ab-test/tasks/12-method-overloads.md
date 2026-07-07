---
name: 12-method-overloads
fixture: nopcommerce
verification:
  - {type: build, expected_exit: 0}
  - {type: file-exists, paths: [OVERLOADS.md]}
  - {type: loc-delta-max, max: 200, ignore_tracked_paths: [OVERLOADS.md]}
---
`ProductService` (in `Nop.Services.Catalog`) declares two overloads of `UpdateProductWarehouseInventoryAsync` — one taking a single warehouse-inventory record and one taking a list of them. You're reviewing how this method family is used across nopCommerce. Without modifying any code, write a markdown report at `OVERLOADS.md` (in the solution root) that treats the two overloads as one set.

The report must document, for the overload set as a whole:

(a) **signatures** — both overloads, their parameter types and return types, each with its `file:line`;
(b) **inbound** — who calls the method, aggregated across both overloads, grouped by project; for each call site note which overload it binds to (the single-record or the list form) and its `file:line`;
(c) **outbound** — the in-solution collaborators each overload calls out to, ignoring BCL/LINQ noise.

Cite `file:line` throughout. Close with a one-paragraph note on whether the two overloads share callers or are used in distinct contexts. Do NOT modify any files other than creating `OVERLOADS.md`. Justify each inbound/outbound claim with evidence (the calling expression at each site).
