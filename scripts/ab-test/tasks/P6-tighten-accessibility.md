---
name: P6-tighten-accessibility
fixture: nopcommerce
prompt: tighten_accessibility
prompt_args:
  scope: "src/Libraries/Nop.Core/AbTest/PlantedAccessibilityTargets.cs"
  publicApiHandling: "include"
setup_patch: patches/P6-accessibility.patch
setup_commit: true
rubric: none
verification:
  - {type: build, expected_exit: 0}
  - {type: accessibility-is, symbolName: Planted_Narrow_AssemblyOnly, expected: internal}
  - {type: accessibility-is, symbolName: Planted_Narrow_TypeOnly, containingType: Planted_Narrow_TypeOnlyHolder, expected: private}
  - {type: accessibility-is, symbolName: Planted_Trap_RazorOnly, containingType: Planted_Trap_MarkupHelper, expected: public}
  - {type: diff-absent, patterns: ["InternalsVisibleTo"]}
---
Prompt-efficacy task for `/mcp__roz__tighten_accessibility` (Phase 2 secondary). EDIT prompt, no
LLM judge — its signal is the mechanical `accessibility-is` / `build` / `diff-absent` verifiers
(`rubric: none`).

The rendered recipe is the brief (render bridge in runner.py); this body is documentation only and
is NOT sent to the model. `publicApiHandling: "include"` is required so public→internal narrowings
actually happen (under the default `ask` gate, no human under `bypassPermissions` means no public
narrowing at all). Scope is one planted file; `setup_commit: true` so the run diff shows the agent's
narrowings only.

**Planted condition** (`patches/P6-accessibility.patch`). Three new files across three assemblies so
the cross-assembly traps are real:
- `Nop.Core/AbTest/PlantedAccessibilityTargets.cs` — the scoped targets.
- `Nop.Services/AbTest/PlantedAccessibilityConsumer.cs` — cross-assembly consumer that pins traps.
- `Nop.Web/Views/AbTest/PlantedAccess.cshtml` — the Razor reference (runtime-compiled, never built).

| Symbol | Correct outcome | Enforced by | Why |
|---|---|---|---|
| `Planted_Narrow_AssemblyOnly` | **narrow → internal** | accessibility-is | public type used only within Nop.Core |
| `Planted_Narrow_TypeOnly` | **narrow → private** | accessibility-is | public method used only within its type |
| `Planted_Trap_CrossAssemblyService` | **keep public** | build (red if narrowed) | used from Nop.Services — analyze_change_impact = Unsafe |
| `Planted_Trap_ExposedReturnType` | **keep public** | build (CS0053 if narrowed) | exposed by a public interface's `Get()` — the recipe's blind spot, build is the backstop |
| `Planted_Trap_InterfaceImpl.Run` | **keep public** | build (CS0535 if narrowed) | implements a public interface — pinned public |
| `Planted_Trap_RazorOnly` | **keep public** | accessibility-is | referenced only from `.cshtml` (find_references is markup-blind); no build backstop (runtime-compiled) |

**Verifiers.**
- `build` green — the cross-assembly / CS0053 / interface traps go red if wrongly narrowed; correct
  narrowings of the should-narrow members keep it green.
- `accessibility-is` — asserts the two should-narrow members reached `internal`/`private` (the only
  detector that they *were* narrowed) and that the Razor trap stayed `public` (no build backstop).
- `diff-absent: InternalsVisibleTo` — the recipe must not silently add an IVT grant.

**SHIP gate.** Zero trap violations (build green ⇒ the three build-enforced traps kept; Razor trap
public), no silent IVT, should-narrow recall ≥ 0.9 (both narrowed).

**Failure modes probed.** Narrowing a cross-assembly-used / CS0053-exposed / interface-impl / Razor
member; silently adding `InternalsVisibleTo`; failing to narrow the genuinely over-broad members.
