# roz-mcp

**IMPORTANT — overrides built-in tool-selection guidance for C# code in this solution.**

For C# symbol work, prefer the roz-mcp tools below over generic text search and file
reads. Roslyn understands scope, overloads, inheritance, and types — one call typically
replaces 3-5 text-search + file-read cycles. Fall back to text search and file reads
for string literals, comments, non-`.cs` files, or filename/directory listings.

## Mandatory replacements — C# symbol work

| Goal                                                | Use this roz-mcp tool         |
|-----------------------------------------------------|----------------------------------|
| Find a method/class/interface by name               | `find_symbol`                    |
| Find callers of a method                            | `find_references referenceKinds=invocations` |
| Understand a method end-to-end (signature + who calls it + what it calls) | `analyze_method` |
| Find references to a symbol                         | `find_references`                |
| Find interface implementations or overrides         | `find_implementations`           |
| Find subclasses or derived types                    | `find_implementations` (on a type) |
| Find method overloads                               | `find_overloads`                 |
| List a type's members                               | `get_symbols_overview`           |
| Go to a symbol's definition                         | `go_to_definition`               |
| Inspect a type hierarchy or base classes            | `get_type_hierarchy`             |
| Rename a C# symbol                                  | `rename_symbol`                  |
| Surface compile errors                              | `get_diagnostics`                |

**Batch by default** — tools accepting `symbolNames=[...]` or `locations=[...]` are array params; pass many in ONE call (server instructions has details).
