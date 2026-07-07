---
name: 10-method-callgraph
fixture: nopcommerce
verification:
  - {type: build, expected_exit: 0}
  - {type: file-exists, paths: [CALL_GRAPH.md]}
  - {type: loc-delta-max, max: 200, ignore_tracked_paths: [CALL_GRAPH.md]}
---
You're tracing the order-placement pipeline in nopCommerce and need to map the call graph of two methods on `OrderProcessingService` (in `Nop.Services.Orders`): `PlaceOrderAsync` and `UpdateOrderTotalsAsync`. Without modifying any code, write a markdown report at `CALL_GRAPH.md` (in the solution root) that maps each method end-to-end.

For each of the two methods, document:

(a) **signature** — the full signature and return type, with its `file:line`;
(b) **inbound** — who calls it, grouped by project (note when a method has no direct callers and is reached only via the `IOrderProcessingService` interface or DI);
(c) **outbound** — every in-solution collaborator it calls out to (other services, repositories, event publishers, helpers), grouped by the target it calls, ignoring BCL/LINQ noise.

Cite `file:line` for every method and for representative inbound and outbound sites. Close with a one-paragraph summary of how the two methods sit in the order pipeline and which collaborators dominate their outbound calls.

Do NOT modify any files other than creating `CALL_GRAPH.md`. Justify each inbound/outbound claim with evidence (caller locations, the called expression at each site).
