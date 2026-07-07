---
name: 14-dead-code-traps
fixture: nopcommerce
setup_patch: patches/P5-dead-code.patch
setup_commit: true
rubric: none
verification:
  - {type: build, expected_exit: 0}
  - {type: token-residual, token: Planted_Dead_UnusedType, max_count: 0}
  - {type: token-residual, token: Planted_Dead_UnusedHelperMethod, max_count: 0}
  - {type: token-residual, token: Planted_Dead_OrphanMethod, max_count: 0}
  - {type: token-residual, token: Planted_Trap_RazorOnly, min_count: 1}
  - {type: token-residual, token: Planted_Trap_DiRegisteredService, min_count: 1}
  - {type: token-residual, token: Planted_Trap_DispatchImpl, min_count: 1}
  - {type: token-residual, token: Planted_Trap_DiStartup, min_count: 1}
  - {type: token-residual, token: Planted_Trap_PublicUnused, min_count: 1}
  - {type: loc-delta-max, max: 80}
---
A previous experiment left a scratch folder at `src/Libraries/Nop.Core/AbTest/` holding several types and members. Some are genuinely dead — nothing in the solution uses them — and should be removed as part of tidying up. Others are still in use and must stay.

Your task: remove every genuinely-unused type and member under `src/Libraries/Nop.Core/AbTest/`, and only those. Leave everything else in the solution exactly as it is.

Before deleting a symbol, satisfy yourself that it is actually unused. A symbol can be reached without any direct textual call to it — for example through framework/runtime mechanisms, dependency-injection registration, markup/views, reflection, or interface dispatch — so a plain text search for its name is not, by itself, proof that it is dead. Deleting something that is still reached will break the build; keeping something that is truly dead leaves the folder untidy.

Done when:
1. `dotnet build src/NopCommerce.sln` exits 0.
2. Every genuinely-dead symbol under `src/Libraries/Nop.Core/AbTest/` has been removed.
3. Every still-reachable symbol under that folder is preserved.

Do NOT modify, create, or delete any file outside `src/Libraries/Nop.Core/AbTest/`.
