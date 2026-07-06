#!/usr/bin/env python3
"""Generate reference oracles for the impact + method-comprehension A/B tasks.

Drives the installed `roz-mcp` as a stdio MCP subprocess against the pinned
nopCommerce clone, calls the per-spec tool (`analyze_change_impact` for the
impact tasks, `analyze_method` for the method-comprehension tasks) once per
positive task, and writes the tool's verbatim output to
`tasks/<task>.reference.md`. Those oracles are the LLM-judge's ground truth
(see judge.py). Both tools are read-only and deterministic, so a generated +
eyeballed reference is a legitimate oracle; re-run this when the pinned clone
SHA bumps.

One warm server serves every call, so the ~35-project workspace cold-loads only
once. ROZ_TOOLS=all is forced because both analyze_change_impact and
analyze_method are held out of the `default` preset — without it neither tool
would be registered.

Usage:
    python scripts/ab-test/generate_references.py                  # all positive tasks
    python scripts/ab-test/generate_references.py --task 06-impact-remove
    python scripts/ab-test/generate_references.py --task 10-method-callgraph
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))

from roslyn_abtest.fixtures import get_fixture  # noqa: E402
from roslyn_abtest.mcp_client import (  # noqa: E402
    CALL_TIMEOUT_S,
    McpStdioClient,
    resolve_roslyn_exe,
)
from roslyn_abtest.paths import TASKS_DIR  # noqa: E402

# High enough to capture every site of the widest symbol (06/07 ~200-275 refs)
# without truncation; paired with a large response cap below.
MAX_RESULTS = 2000


@dataclass(frozen=True)
class OracleSpec:
    """One reference-oracle generation: the tool, its args, and a provenance header."""

    task: str
    header: str
    arguments: dict
    tool: str = "analyze_change_impact"


# Each spec mirrors the matching task brief in tasks/<task>.md. The header is
# written verbatim at the top of the reference so the exact modeled change (and
# any free choice, e.g. 04's newType) is self-documenting.
SPECS: list[OracleSpec] = [
    OracleSpec(
        task="04-impact-analysis",
        header=(
            "TypeChange on IRepository<TEntity>.GetByIdAsync return "
            "`Task<TEntity>` -> `Task<BaseEntity>` (a widening). newType is a free "
            "choice per the plan: it shifts verdict tags, not which sites appear, "
            "so site-recall is unaffected by the exact type."
        ),
        arguments={
            "symbolNames": ["GetByIdAsync"],
            "containingType": "IRepository",
            "changeKind": "TypeChange",
            "newType": "Task<BaseEntity>",
            "maxResults": MAX_RESULTS,
        },
    ),
    OracleSpec(
        task="06-impact-remove",
        header="RemoveSymbol on IStaticCacheManager.RemoveByPrefixAsync (Nop.Core).",
        arguments={
            "symbolNames": ["RemoveByPrefixAsync"],
            "containingType": "IStaticCacheManager",
            "changeKind": "RemoveSymbol",
            "maxResults": MAX_RESULTS,
        },
    ),
    OracleSpec(
        task="07-impact-accessibility",
        header=(
            "AccessibilityNarrow on ILocalizationService.GetLocalizedAsync "
            "(Nop.Services): public -> internal. Chosen for a genuine same/cross-"
            "assembly split (Nop.Services callers stay Compatible, Nop.Web/Framework/"
            "plugin callers become Unsafe). The plan's IWorkContext.GetCurrentCustomerAsync "
            "had 0 same-assembly callers, so its partition was degenerate (all-Unsafe)."
        ),
        arguments={
            "symbolNames": ["GetLocalizedAsync"],
            "containingType": "ILocalizationService",
            "changeKind": "AccessibilityNarrow",
            "newAccessibility": "Internal",
            "maxResults": MAX_RESULTS,
        },
    ),
    OracleSpec(
        task="08-impact-signature",
        header=(
            "SignatureChange on IProductService.GetProductByIdAsync (Nop.Services): "
            "v1 SignatureChange is coarse — every call site is requires-update."
        ),
        arguments={
            "symbolNames": ["GetProductByIdAsync"],
            "containingType": "IProductService",
            "changeKind": "SignatureChange",
            "maxResults": MAX_RESULTS,
        },
    ),
    # --- analyze_method oracles (method-comprehension A/B) -------------------
    # symbolNames target the CONCRETE ProductService/OrderProcessingService so the
    # outbound section is populated from the method body; the reused inbound search
    # (find_references referenceKinds=invocations) still resolves interface-dispatched callers.
    # Task 11 deliberately targets the INTERFACE method to exercise inbound-at-scale.
    OracleSpec(
        task="05-explain-service",
        header=(
            "analyze_method over 8 representative ProductService public methods "
            "(Nop.Services.Catalog) — the catalog CRUD + core lookups + inventory an "
            "onboarding doc would cover. Inbound is interface-dispatched via IProductService; "
            "outbound is the in-solution collaborator set per method. The candidate is free to "
            "pick its own '8 most important' — recall is graded per matched method, and both "
            "arms face the same oracle, so any method-set drift biases the arms equally."
        ),
        arguments={
            "symbolNames": [
                "GetProductByIdAsync",
                "GetProductsByIdsAsync",
                "SearchProductsAsync",
                "InsertProductAsync",
                "UpdateProductAsync",
                "DeleteProductAsync",
                "GetProductBySkuAsync",
                "AdjustInventoryAsync",
            ],
            "containingType": "ProductService",
            "maxResults": MAX_RESULTS,
        },
        tool="analyze_method",
    ),
    OracleSpec(
        task="10-method-callgraph",
        header=(
            "analyze_method on OrderProcessingService.PlaceOrderAsync + "
            "UpdateOrderTotalsAsync (Nop.Services.Orders) — the outbound differentiator: "
            "both are god-class pipeline methods with a rich in-solution call graph. "
            "Outbound (grouped by target) is the load-bearing section to eyeball."
        ),
        arguments={
            "symbolNames": ["PlaceOrderAsync", "UpdateOrderTotalsAsync"],
            "containingType": "OrderProcessingService",
            "maxResults": MAX_RESULTS,
        },
        tool="analyze_method",
    ),
    OracleSpec(
        task="11-method-interface",
        header=(
            "analyze_method on IProductService.GetProductByIdAsync (Nop.Services.Catalog) — "
            "the interface declaration, chosen for high inbound fan-in. Outbound is empty "
            "(no body on an interface member); the oracle is the full interface-dispatched "
            "caller list plus the interface tip / DI fallback."
        ),
        arguments={
            "symbolNames": ["GetProductByIdAsync"],
            "containingType": "IProductService",
            "maxResults": MAX_RESULTS,
        },
        tool="analyze_method",
    ),
    OracleSpec(
        task="12-method-overloads",
        header=(
            "analyze_method with includeOverloads on "
            "ProductService.UpdateProductWarehouseInventoryAsync (Nop.Services.Catalog) — "
            "a genuine overload pair (single ProductWarehouseInventory vs IList<…>). "
            "Aggregates callers/callees across both overloads and appends the overload list."
        ),
        arguments={
            "symbolNames": ["UpdateProductWarehouseInventoryAsync"],
            "containingType": "ProductService",
            "includeOverloads": True,
            "maxResults": MAX_RESULTS,
        },
        tool="analyze_method",
    ),
]


@dataclass(frozen=True)
class BreakingChange:
    """One planted public-surface change in the check_breaking_changes scenario.

    `arguments` is the `analyze_change_impact` call that enumerates the change's
    verified in-solution sites; `break_class` / `note` are the hand-annotated
    ground truth the `breaking` judge rubric grades against."""

    label: str
    break_class: str
    note: str
    arguments: dict


# Task stem -> the planted public-surface changes whose in-solution impact seeds its
# oracle. These are forward-modeled against the CLEAN pinned clone (the symbols still
# exist there): analyze_change_impact has no 'behavioral' changeKind, and a removed
# symbol can't be resolved post-patch, so the oracle models each change the way
# assess_impact would — before it was applied — mirroring patches/P3-breaking-changes.patch.
BREAKING_ORACLES: dict[str, list[BreakingChange]] = {
    "P3-check-breaking-changes": [
        BreakingChange(
            label="CommonHelper.AreNullOrEmpty(params string[]) — REMOVED",
            break_class="source-incompatible (external-surface only)",
            note=(
                "No in-solution callers, so removal breaks only out-of-solution consumers; "
                "the tool confirms 0 in-solution sites."
            ),
            arguments={
                "symbolNames": ["AreNullOrEmpty"],
                "containingType": "CommonHelper",
                "changeKind": "RemoveSymbol",
                "includeTests": True,
                "maxResults": MAX_RESULTS,
            },
        ),
        BreakingChange(
            label=(
                "CommonHelper.GenerateRandomDigitCode(int) — added optional "
                "`bool avoidLeadingZero = false`"
            ),
            break_class=(
                "binary-incompatible (source still compiles via the default; "
                "already-compiled callers break)"
            ),
            note=(
                "Optional trailing parameter: in-solution call sites recompile unchanged, but "
                "the method signature changed, so binary consumers must rebuild. SignatureChange "
                "is coarse — it tags every call site requires-update."
            ),
            arguments={
                "symbolNames": ["GenerateRandomDigitCode"],
                "containingType": "CommonHelper",
                "changeKind": "SignatureChange",
                "includeTests": True,
                "maxResults": MAX_RESULTS,
            },
        ),
        BreakingChange(
            label="CommonHelper.IsValidIpAddress(string) — body now IPv4-only (was any IP family)",
            break_class="behavior change (identical signature, different semantics)",
            note=(
                "No signature/type/visibility change, so analyze_change_impact has no verdict "
                "here; SignatureChange is used only to ENUMERATE the call sites whose observed "
                "behavior shifts."
            ),
            arguments={
                "symbolNames": ["IsValidIpAddress"],
                "containingType": "CommonHelper",
                "changeKind": "SignatureChange",
                "includeTests": True,
                "maxResults": MAX_RESULTS,
            },
        ),
    ],
}

# Per breaking task: the non-public change(s) that appear in the diff but MUST NOT be
# reported (a candidate that flags one is over-reporting). Folded into the reference so
# the judge can penalize a misclassified internal change.
BREAKING_DECOYS: dict[str, str] = {
    "P3-check-breaking-changes": (
        "EncryptionKeyMetadata (internal static class, "
        "src/Libraries/Nop.Core/Security/EncryptionKeyMetadata.cs) — INTERNAL surface added "
        "on the branch. Internal members cannot break external consumers, so a breaking-change "
        "report must NOT flag it; listing it is over-reporting."
    ),
}


def _reference_path(task: str) -> Path:
    return TASKS_DIR / f"{task}.reference.md"


def _generate_breaking_reference(client: McpStdioClient, task: str) -> Path:
    """Run analyze_change_impact per planted change and write the combined breaking oracle.

    Unlike the single-call specs, a breaking oracle pools several forward-modeled changes
    (RemoveSymbol / SignatureChange) into one reference, prepends a hand-annotated break-class
    table, and folds in the internal decoy that must be ignored."""
    changes = BREAKING_ORACLES[task]
    decoy = BREAKING_DECOYS.get(task, "")

    sections: list[str] = []
    for index, change in enumerate(changes, 1):
        print(f"  [{index}/{len(changes)}] {change.label}", flush=True)
        body = client.call_tool("analyze_change_impact", change.arguments, CALL_TIMEOUT_S)
        sections.append(
            f"=== Change {index}: {change.label} ===\n"
            f"Break class (hand-annotated ground truth): {change.break_class}\n"
            f"{change.note}\n\n"
            f"{body.rstrip()}"
        )

    table = [
        "| # | Planted public-surface change | Break class |",
        "|---|-------------------------------|-------------|",
    ]
    for index, change in enumerate(changes, 1):
        table.append(f"| {index} | {change.label} | {change.break_class} |")

    args_dump = json.dumps([c.arguments for c in changes], sort_keys=True)
    decoy_block = f"**Decoy — must NOT be reported as breaking:** {decoy}\n\n" if decoy else ""
    document = (
        "<!-- GENERATED by scripts/ab-test/generate_references.py — do not edit by hand.\n"
        "     check_breaking_changes oracle: each planted change forward-modeled on the CLEAN\n"
        "     pinned clone (run against an un-patched clone). Break classes hand-annotated.\n"
        f"     analyze_change_impact arguments: {args_dump} -->\n\n"
        "# Planted breaking-change set (vs the pinned baseline)\n\n"
        + "\n".join(table)
        + "\n\n"
        + decoy_block
        + "\n\n".join(sections)
        + "\n"
    )
    out_path = _reference_path(task)
    out_path.write_text(document, encoding="utf-8")
    return out_path


# --- 02-audit oracle (bespoke, multi-tool) -----------------------------------
# Grounds all three rankings in the roz-mcp tools' own output (the legitimate-oracle
# principle from judge.py). find_references with maxResults=1 forces truncation, so the
# header carries the true total ("showing 1 of N") AND the per-project scope ("across F
# files, P projects") — both computed over ALL references before truncation — with a tiny
# response. Candidate discovery (which interfaces/files exist) is deterministic Python;
# the RANKING signal (counts, project-spread) is always the tool's.

AUDIT_TASK = "02-audit"
_AUDIT_FANIN_TOP = 15
_AUDIT_ENTITY_TOP = 10
_AUDIT_GODCLASS_TOP = 10
_AUDIT_REF_MAXRESULTS = 1      # force truncation so the header emits total + project count
_AUDIT_IMPL_MAXRESULTS = 2000  # high enough that find_implementations(BaseEntity) isn't truncated
_AUDIT_CHUNK = 20              # symbolNames per batched find_references call

# find_references header, single or batched: "References to 'X' (showing 1 of N location(s)
# across F files, P projects):". The scope clause is present only on a truncated result.
_REF_HEADER_RE = re.compile(
    r"References to '([^']+)' \((?:showing \d+ of )?(\d+) location\(s\)"
    r"(?: across (\d+) files, (\d+) projects)?\)"
)
# find_implementations derived-class tree line: "{indent/glyphs}{TypeName} (path:line)".
# Capture the location too: entity references are looked up by CURSOR, not by name —
# common entity names (Order matches 54 symbols, Customer 31) are hopelessly ambiguous,
# so a bare-name find_references errors and drops exactly the entities that matter.
_TREE_TYPE_RE = re.compile(r"^[\s│├└─]*([A-Za-z_]\w*) \(([^)]+)\)")
# get_symbols_overview member line (depth>=1) carries an "[access kind]" tag.
_MEMBER_TAG_RE = re.compile(
    r"\[(?:public|private protected|protected internal|protected|private|internal|file)\s"
)

_GENERATED_MARKERS = (".designer.cs", ".g.cs", ".generated.cs", "assemblyinfo.cs")
_EXCLUDED_DIR_PARTS = ("/obj/", "/bin/", "/migrations/", "/.git/", "/tests/")


def _chunked(items: list[str], size: int) -> list[list[str]]:
    return [items[i:i + size] for i in range(0, len(items), size)]


def _parse_ref_headers(text: str) -> dict[str, tuple[int, int]]:
    """Map symbol name -> (total_references, project_count) from find_references output.

    A symbol with <=1 reference isn't truncated, so the scope clause (and its project
    count) is absent; treat those as spanning a single project."""
    out: dict[str, tuple[int, int]] = {}
    for name, total, _files, projects in _REF_HEADER_RE.findall(text):
        out[name] = (int(total), int(projects) if projects else 1)
    return out


def _ref_counts(
    client: McpStdioClient, items: list[str], arg_key: str = "symbolNames"
) -> dict[str, tuple[int, int]]:
    """Batch find_references over names or locations (maxResults=1); parse (refs, projects).

    `arg_key` is "symbolNames" (services) or "locations" (entities, resolved by cursor to
    dodge name ambiguity). Results are keyed by the RESOLVED symbol name from each header."""
    counts: dict[str, tuple[int, int]] = {}
    for chunk in _chunked(items, _AUDIT_CHUNK):
        body = client.call_tool(
            "find_references",
            {arg_key: chunk, "maxResults": _AUDIT_REF_MAXRESULTS, "includeTests": True},
            CALL_TIMEOUT_S,
        )
        counts.update(_parse_ref_headers(body))
    return counts


def _discover_service_interfaces(services_dir: Path) -> list[str]:
    """Scan a Nop.Services tree for `interface I<Name>Service` declarations (deterministic)."""
    pattern = re.compile(r"\binterface\s+(I[A-Za-z0-9]+Service)\b")
    names: set[str] = set()
    if services_dir.is_dir():
        for cs in services_dir.rglob("*.cs"):
            try:
                names.update(pattern.findall(cs.read_text(encoding="utf-8", errors="replace")))
            except OSError:
                continue
    return sorted(names)


def _discover_entities(client: McpStdioClient) -> list[str]:
    """Enumerate BaseEntity-derived type declaration LOCATIONS via find_implementations.

    Returns each derived type's `path:line` cursor (not its name) so references can be
    looked up unambiguously. The FQN `Nop.Core.BaseEntity` disambiguates the class from a
    same-named test property (`CrudData<TEntity>.BaseEntity`); a bare `BaseEntity` errors."""
    body = client.call_tool(
        "find_implementations",
        {"symbolNames": ["Nop.Core.BaseEntity"], "maxResults": _AUDIT_IMPL_MAXRESULTS},
        CALL_TIMEOUT_S,
    )
    locations: list[str] = []
    seen: set[str] = set()
    for line in body.splitlines():
        m = _TREE_TYPE_RE.match(line)
        if not m or m.group(1) == "BaseEntity":
            continue
        location = m.group(2).strip()
        # Skip metadata types (" (external)") and dedupe by declaration site.
        if ":" in location and location not in seen:
            seen.add(location)
            locations.append(location)
    return locations


def _rank_god_classes(src_dir: Path, top: int) -> list[tuple[str, int]]:
    """Rank source .cs files by line count, excluding generated/vendored/test files."""
    ranked: list[tuple[str, int]] = []
    for cs in src_dir.rglob("*.cs"):
        posix = cs.as_posix().lower()
        if any(part in posix for part in _EXCLUDED_DIR_PARTS) or posix.endswith(_GENERATED_MARKERS):
            continue
        try:
            with cs.open(encoding="utf-8", errors="replace") as fh:
                loc = sum(1 for _ in fh)
        except OSError:
            continue
        ranked.append((cs.relative_to(src_dir).as_posix(), loc))
    ranked.sort(key=lambda pair: pair[1], reverse=True)
    return ranked[:top]


def _god_class_member_counts(
    client: McpStdioClient, rel_paths: list[str]
) -> dict[str, int]:
    """Best-effort member count per file via get_symbols_overview (depth=1)."""
    counts: dict[str, int] = {}
    for rel in rel_paths:
        try:
            body = client.call_tool(
                "get_symbols_overview",
                {"filePaths": [rel], "depth": 1, "maxMembers": 2000},
                CALL_TIMEOUT_S,
            )
        except (RuntimeError, TimeoutError):
            continue
        counts[rel] = len(_MEMBER_TAG_RE.findall(body))
    return counts


def _build_audit_document(
    task: str,
    services: list[tuple[str, tuple[int, int]]],
    entities: list[tuple[str, tuple[int, int]]],
    god_classes: list[tuple[str, int]],
    members: dict[str, int],
    service_pool: int,
    entity_pool: int,
) -> str:
    """Assemble the 02-audit ground-truth document: three ranked tables + provenance."""
    fanin_rows = [
        f"| {rank} | {name} | {refs} | {projects} |"
        for rank, (name, (refs, projects)) in enumerate(services, 1)
    ]
    entity_rows = [
        f"| {rank} | {name} | {projects} | {refs} |"
        for rank, (name, (refs, projects)) in enumerate(entities, 1)
    ]
    god_rows = [
        f"| {rank} | {rel} | {loc} | {members.get(rel, '-')} |"
        for rank, (rel, loc) in enumerate(god_classes, 1)
    ]
    return (
        "<!-- GENERATED by scripts/ab-test/generate_references.py — do not edit by hand.\n"
        "     02-audit oracle. Rankings are the roz-mcp tools' own output against the pinned\n"
        "     clone; candidate discovery (interface/file enumeration) is deterministic.\n"
        f"     Fan-in: {service_pool} Nop.Services I*Service interfaces scored by find_references\n"
        "       (referenceKinds=all, includeTests, maxResults=1 -> header total).\n"
        f"     Entities: {entity_pool} BaseEntity subtypes (find_implementations) scored by the\n"
        "       per-project spread of find_references (header 'across F files, P projects').\n"
        "     God-classes: source .cs ranked by line count (generated/test/obj excluded),\n"
        "       member counts via get_symbols_overview depth=1. -->\n\n"
        "# nopCommerce audit — ground truth (vs the pinned baseline)\n\n"
        "## (a) Top service interfaces by fan-in (callers across the solution)\n\n"
        "| Rank | Service interface | Callers (refs) | Projects |\n"
        "|------|-------------------|----------------|----------|\n"
        + "\n".join(fanin_rows)
        + "\n\n## (b) Entities by project spread (referenced from the most projects)\n\n"
        "| Rank | Entity | Projects | Refs |\n"
        "|------|--------|----------|------|\n"
        + "\n".join(entity_rows)
        + "\n\n## (c) Biggest god-classes by LOC\n\n"
        "| Rank | File | LOC | ~Members |\n"
        "|------|------|-----|----------|\n"
        + "\n".join(god_rows)
        + "\n"
    )


def _generate_audit_reference(client: McpStdioClient, task: str, fixture: object) -> Path:
    """Drive the roz-mcp tools to build the 02-audit ground-truth oracle and write it."""
    src_dir = fixture.solution_path.parent  # type: ignore[attr-defined]
    services_dir = src_dir / "Libraries" / "Nop.Services"

    print("  [1/3] fan-in: enumerating Nop.Services interfaces + find_references...", flush=True)
    service_names = _discover_service_interfaces(services_dir)
    service_counts = _ref_counts(client, service_names)
    top_services = sorted(
        service_counts.items(), key=lambda kv: kv[1][0], reverse=True
    )[:_AUDIT_FANIN_TOP]

    print("  [2/3] entities: find_implementations(BaseEntity) + project spread...", flush=True)
    entity_locations = _discover_entities(client)
    entity_counts = _ref_counts(client, entity_locations, arg_key="locations")
    top_entities = sorted(
        entity_counts.items(), key=lambda kv: (kv[1][1], kv[1][0]), reverse=True
    )[:_AUDIT_ENTITY_TOP]

    print("  [3/3] god-classes: LOC ranking + get_symbols_overview member counts...", flush=True)
    god_classes = _rank_god_classes(src_dir, _AUDIT_GODCLASS_TOP)
    members = _god_class_member_counts(client, [rel for rel, _ in god_classes])

    document = _build_audit_document(
        task, top_services, top_entities, god_classes, members,
        len(service_names), len(entity_locations),
    )
    out_path = _reference_path(task)
    out_path.write_text(document, encoding="utf-8")
    return out_path


def _write_reference(spec: OracleSpec, body: str) -> Path:
    """Wrap the tool output with a provenance header and write the .reference.md."""
    out_path = _reference_path(spec.task)
    args_line = json.dumps(spec.arguments, sort_keys=True)
    document = (
        f"<!-- GENERATED by scripts/ab-test/generate_references.py — do not edit by hand.\n"
        f"     {spec.header}\n"
        f"     {spec.tool} arguments: {args_line} -->\n\n"
        f"{body.rstrip()}\n"
    )
    out_path.write_text(document, encoding="utf-8")
    return out_path


def main() -> None:
    parser = argparse.ArgumentParser(prog="generate_references")
    parser.add_argument(
        "--task",
        nargs="+",
        help="Generate only these tasks' references (default: all positive tasks). "
        "Pass several to regenerate a subset off ONE warm server (one cold load).",
    )
    args = parser.parse_args()

    known_tasks = [s.task for s in SPECS] + list(BREAKING_ORACLES) + [AUDIT_TASK]
    if args.task:
        requested = set(args.task)
        missing = requested - set(known_tasks)
        if missing:
            sys.exit(
                f"Unknown task(s) {sorted(missing)}. Known: {', '.join(known_tasks)}"
            )
    else:
        requested = set(known_tasks)
    specs = [s for s in SPECS if s.task in requested]
    breaking_tasks = [t for t in BREAKING_ORACLES if t in requested]
    audit_requested = AUDIT_TASK in requested
    total = len(specs) + len(breaking_tasks) + (1 if audit_requested else 0)

    fixture = get_fixture("nopcommerce")
    if not fixture.solution_path.is_file():
        sys.exit(
            f"Clone solution not found at {fixture.solution_path}. Acquire the "
            f"fixture first (build the stress tests, or run a smoke A/B task)."
        )

    env = {
        **os.environ,
        "ROZ_SOLUTION_PATH": str(fixture.solution_path),
        "ROZ_TOOLS": "all",  # analyze_change_impact is held out of `default`
        "ROZ_MAX_RESPONSE_CHARS": "300000",
        # Pin the log level: **os.environ above leaks an ambient Information level, ballooning
        # the per-file reload log to hundreds of MB across a churny generation run.
        "ROZ_LOG_LEVEL": "Warning",
    }

    exe = resolve_roslyn_exe()
    print(f"Launching {exe}", flush=True)
    print(f"  solution: {fixture.solution_path}", flush=True)
    client = McpStdioClient(exe, env)
    failures = 0
    try:
        print("Handshaking (cold workspace load happens on the first call)...", flush=True)
        client.initialize()
        for spec in specs:
            print(f"\n=== {spec.task} ===", flush=True)
            print(f"  args: {json.dumps(spec.arguments)}", flush=True)
            start = time.monotonic()
            try:
                body = client.call_tool(
                    spec.tool, spec.arguments, CALL_TIMEOUT_S
                )
            except (RuntimeError, TimeoutError) as exc:
                failures += 1
                print(f"  FAILED ({time.monotonic() - start:.0f}s): {exc}", flush=True)
                continue
            out_path = _write_reference(spec, body)
            print(
                f"  ok ({time.monotonic() - start:.0f}s): "
                f"{len(body)} chars -> {out_path}",
                flush=True,
            )
        for task in breaking_tasks:
            print(
                f"\n=== {task} (breaking oracle: {len(BREAKING_ORACLES[task])} changes) ===",
                flush=True,
            )
            start = time.monotonic()
            try:
                out_path = _generate_breaking_reference(client, task)
            except (RuntimeError, TimeoutError) as exc:
                failures += 1
                print(f"  FAILED ({time.monotonic() - start:.0f}s): {exc}", flush=True)
                continue
            print(f"  ok ({time.monotonic() - start:.0f}s): -> {out_path}", flush=True)
        if audit_requested:
            print(
                f"\n=== {AUDIT_TASK} (audit oracle: fan-in + entities + god-classes) ===",
                flush=True,
            )
            start = time.monotonic()
            try:
                out_path = _generate_audit_reference(client, AUDIT_TASK, fixture)
                print(f"  ok ({time.monotonic() - start:.0f}s): -> {out_path}", flush=True)
            except (RuntimeError, TimeoutError) as exc:
                failures += 1
                print(f"  FAILED ({time.monotonic() - start:.0f}s): {exc}", flush=True)
    finally:
        client.close()

    if failures:
        sys.exit(f"{failures}/{total} reference(s) failed — see output above.")
    print("\nAll references generated. Eyeball each before trusting it.", flush=True)


if __name__ == "__main__":
    main()
