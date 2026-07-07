from __future__ import annotations

import sys
from pathlib import Path
from types import SimpleNamespace

# generate_references.py is a top-level script under scripts/ab-test/, not in the package;
# import it by path-insertion the same way it bootstraps itself.
_SCRIPT_DIR = Path(__file__).resolve().parent.parent
if str(_SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(_SCRIPT_DIR))

import generate_references as genref  # noqa: E402


# --------------------------------- pure parsers ---------------------------------


def test_parse_ref_headers_extracts_total_and_project_count() -> None:
    text = (
        "References to 'ICustomerService' (showing 1 of 512 location(s) "
        "across 90 files, 14 projects):\n"
        "  Distribution:\n    Nop.Services  400  (60 files)\n"
        "=== IProductService ===\n"
        "References to 'IProductService' (showing 1 of 640 location(s) "
        "across 120 files, 18 projects):\n"
    )
    out = genref._parse_ref_headers(text)
    assert out["ICustomerService"] == (512, 14)
    assert out["IProductService"] == (640, 18)


def test_parse_ref_headers_single_ref_defaults_project_count_to_one() -> None:
    # A symbol with <=1 reference isn't truncated -> no "across ... projects" scope clause.
    out = genref._parse_ref_headers("References to 'IRareService' (1 location(s)):")
    assert out["IRareService"] == (1, 1)


def test_discover_service_interfaces_finds_declarations(tmp_path: Path) -> None:
    svc = tmp_path / "Nop.Services" / "Catalog"
    svc.mkdir(parents=True)
    (svc / "IProductService.cs").write_text(
        "namespace X;\npublic partial interface IProductService { }\n", encoding="utf-8"
    )
    (tmp_path / "Nop.Services" / "ICustomerService.cs").write_text(
        "public interface ICustomerService { }\n", encoding="utf-8"
    )
    (svc / "Helper.cs").write_text("public class ProductServiceHelper { }\n", encoding="utf-8")
    names = genref._discover_service_interfaces(tmp_path / "Nop.Services")
    assert names == ["ICustomerService", "IProductService"]  # sorted; the class is not an interface


def test_discover_entities_parses_declaration_locations_from_tree() -> None:
    body = (
        "Derived classes of 'BaseEntity' (4):\n"
        "\n"
        "Customer (Libraries/Nop.Core/Domain/Customers/Customer.cs:8)\n"
        "Product (Libraries/Nop.Core/Domain/Catalog/Product.cs:10)\n"
        "├─ SubProduct (Libraries/Nop.Core/Domain/Catalog/SubProduct.cs:3)\n"
        "Widget (external)\n"  # metadata type — no path:line, must be skipped
    )
    client = SimpleNamespace(call_tool=lambda name, args, timeout: body)
    # Returns declaration cursors (path:line), not names; the external type is dropped.
    assert genref._discover_entities(client) == [
        "Libraries/Nop.Core/Domain/Customers/Customer.cs:8",
        "Libraries/Nop.Core/Domain/Catalog/Product.cs:10",
        "Libraries/Nop.Core/Domain/Catalog/SubProduct.cs:3",
    ]


def test_rank_god_classes_ranks_by_loc_excluding_generated_and_tests(tmp_path: Path) -> None:
    (tmp_path / "Big.cs").write_text("\n".join(["x"] * 100), encoding="utf-8")
    (tmp_path / "Small.cs").write_text("a\nb\n", encoding="utf-8")
    (tmp_path / "Thing.Designer.cs").write_text("\n".join(["y"] * 500), encoding="utf-8")
    obj = tmp_path / "obj"
    obj.mkdir()
    (obj / "Gen.cs").write_text("\n".join(["z"] * 999), encoding="utf-8")
    tests = tmp_path / "Tests"
    tests.mkdir()
    (tests / "HugeTests.cs").write_text("\n".join(["t"] * 800), encoding="utf-8")
    ranked = genref._rank_god_classes(tmp_path, top=10)
    # Generated (.Designer.cs), obj/, and Tests/ are excluded; ordered by LOC desc.
    assert [rel for rel, _ in ranked] == ["Big.cs", "Small.cs"]
    assert ranked[0][1] == 100


# ------------------------- end-to-end generator (mocked client) -----------------


def test_generate_audit_reference_call_sequence_and_document(tmp_path, monkeypatch) -> None:
    src = tmp_path / "src"
    services = src / "Libraries" / "Nop.Services"
    services.mkdir(parents=True)
    (services / "IFooService.cs").write_text("public interface IFooService{}", encoding="utf-8")
    (services / "IBarService.cs").write_text("public interface IBarService{}", encoding="utf-8")
    (src / "GodClass.cs").write_text("\n".join(["line"] * 300), encoding="utf-8")

    calls: list[str] = []

    def fake_call_tool(name: str, args: dict, timeout: float) -> str:
        calls.append(name)
        if name == "find_implementations":
            return (
                "Derived classes of 'BaseEntity' (2):\n\n"
                "Order (src/Domain/Order.cs:1)\nCustomer (src/Domain/Customer.cs:1)\n"
            )
        if name == "find_references":
            if "symbolNames" in args:  # services, by name
                keys = list(args["symbolNames"])
            else:  # entities, by cursor -> resolved name is the file stem
                keys = [
                    loc.replace("\\", "/").rsplit("/", 1)[-1].split(".cs")[0]
                    for loc in args["locations"]
                ]
            lines = [
                f"References to '{key}' (showing 1 of {100 + i} location(s) "
                f"across 10 files, {2 + i} projects):"
                for i, key in enumerate(keys)
            ]
            return "\n".join(lines)
        if name == "get_symbols_overview":
            return "[public method] void A()\n[private field] int _b\n[public property] int C\n"
        return ""

    client = SimpleNamespace(call_tool=fake_call_tool)
    monkeypatch.setattr(genref, "TASKS_DIR", tmp_path)
    fixture = SimpleNamespace(solution_path=src / "NopCommerce.sln")

    out = genref._generate_audit_reference(client, "02-audit", fixture)
    text = out.read_text(encoding="utf-8")

    assert out == tmp_path / "02-audit.reference.md"
    # Ordered tool sequence: services find_references -> entity find_implementations ->
    # entity find_references -> god-class get_symbols_overview.
    distinct_order = [n for i, n in enumerate(calls) if i == 0 or calls[i - 1] != n]
    assert distinct_order == [
        "find_references", "find_implementations", "find_references", "get_symbols_overview"
    ]
    # Provenance + all three tables + discovered items.
    assert "GENERATED by" in text
    assert "Top service interfaces by fan-in" in text
    assert "Entities by project spread" in text
    assert "Biggest god-classes by LOC" in text
    assert "IFooService" in text and "IBarService" in text
    assert "Order" in text and "Customer" in text
    assert "GodClass.cs" in text
    assert "300" in text  # god-class LOC
