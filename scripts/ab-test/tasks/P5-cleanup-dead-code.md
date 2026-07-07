---
name: P5-cleanup-dead-code
fixture: nopcommerce
prompt: cleanup_dead_code
prompt_args:
  scope: "src/Libraries/Nop.Core/AbTest/PlantedCleanupTargets.cs"
  publicApiHandling: "ask"
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
Prompt-efficacy task for `/mcp__roz__cleanup_dead_code` (Phase 2 secondary). EDIT prompt, no LLM
judge — its signal is the mechanical `token-residual` verifiers (`rubric: none`).

The rendered recipe is the brief (render bridge in runner.py); this body is documentation only and
is NOT sent to the model. Scope is a single planted file so the recipe only touches planted code;
`setup_commit: true` commits the patch so the run diff shows the agent's removals only.

**Planted condition** (`patches/P5-dead-code.patch`, `src/Libraries/Nop.Core/AbTest/`). One C# file
plus a `.cshtml`. The reachable traps are deliberately `internal` so the public-API gate can't save
them — only the correct reasoning keeps each:

| Symbol | Kind | Correct outcome | Why |
|---|---|---|---|
| `Planted_Dead_UnusedType` | internal type | **REMOVE** | no references anywhere |
| `Planted_Dead_UnusedHelperMethod` | internal method | **REMOVE** | no references anywhere |
| `Planted_Dead_OrphanMethod` | internal method in a live public type | **REMOVE** | dead member, surviving container — surgical removal |
| `Planted_Trap_RazorOnly` | internal method | **KEEP** | referenced only from `PlantedRazorView.cshtml` (find_references is markup-blind) |
| `Planted_Trap_DiRegisteredService` | internal type | **KEEP** | DI-registered (its only use is `AddScoped<…>`) |
| `Planted_Trap_DispatchImpl` | internal type | **KEEP** | registered + reached by interface dispatch |
| `Planted_Trap_DiStartup` | internal `INopStartup` | **KEEP** | reflection-scanned framework entry point |
| `Planted_Trap_PublicUnused` | **public** method | **KEEP** | genuinely unused, but behind the ask gate — no human under `bypassPermissions`, so do not delete |

**Verifiers.**
- `build` green — removing the dead set must not break the compile; wrongly deleting a referenced
  trap (DI/dispatch/framework) breaks it.
- `token-residual` `max_count: 0` for each `Planted_Dead_*` (gone) and `min_count: 1` for each
  `Planted_Trap_*` (survives). `count_token_in_src` scans `.cs` only, so the markup reference does
  not inflate the `Planted_Trap_RazorOnly` count — `min_count: 1` tracks its `.cs` declaration.
- `loc-delta-max: 80` bounds collateral (the three dead symbols are ~20 LOC).

**SHIP gate.** Zero trap violations (every `Planted_Trap_*` survives), build green, dead-symbol
recall ≥ 0.9 (all three `Planted_Dead_*` removed).

**Failure modes probed.** Deleting a Razor-live / DI-registered / dispatch / framework-entry / public
symbol; missing a genuinely-dead symbol; breaking the build by deleting a referenced type.
