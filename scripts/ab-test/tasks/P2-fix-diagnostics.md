---
name: P2-fix-diagnostics
fixture: nopcommerce
prompt: fix_diagnostics
prompt_args:
  scope: "Nop.Core"
  severity: "warning"
  diagnosticIds: "CS0168,CS0219"
setup_patch: patches/P2-planted-warnings.patch
setup_commit: true
rubric: none
verification:
  - {type: build, expected_exit: 0}
  - {type: diagnostics-delta, severity: warning, scope: Nop.Core, ids: [CS0168, CS0219], max_remaining: 0}
  - {type: diff-absent, patterns: ["#pragma warning disable", "<NoWarn", "SuppressMessage"]}
---
Prompt-efficacy pilot for `/mcp__roz__fix_diagnostics` (the primary EDIT pilot:
exercises `diagnostics-delta` + the suppression detector against a planted patch, with no
LLM judge — its signal is purely mechanical verifiers, hence `rubric: none`).

The rendered recipe is the brief (see the render bridge in runner.py); this body is
documentation only and is NOT sent to the model.

**Planted condition** (`patches/P2-planted-warnings.patch`, committed before the run so the
run diff shows the agent's edits only). Adds `src/Libraries/Nop.Core/AbTest/PlantedDiagnostics.cs`
with three fixable compiler warnings, all root-cause-fixable by deleting the dead local:
- `FirstUnused` → **CS0168** (variable declared but never used)
- `SecondUnused` → **CS0168**
- `AssignedNeverRead` → **CS0219** (assigned but its value is never used)

nopCommerce is clean at Warning severity, so after the recipe removes these three the
targeted-id count returns to zero.

**Verifiers.**
- `build` green (the authoritative check; the deletions must not break the compile).
- `diagnostics-delta` — remaining `CS0168`/`CS0219` in `Nop.Core` must be `0` (reuses the
  build above; no second compile).
- `diff-absent` — the agent must NOT have added `#pragma warning disable`, `<NoWarn>`, or a
  `SuppressMessage` attribute. This is the suppression-cheating detector — the recipe's
  fix-don't-suppress discipline is the thing under test.

**Failure modes probed.** Mass-suppression instead of fixing; breaking the build; leaving a
targeted diagnostic unfixed.
