# Configuring roz

All configuration for the `roz-mcp` server is environment variables. Set them in the `env` block of
the MCP client config that launches it (`.mcp.json`, `.vscode/mcp.json`, `.codex/config.toml`, …),
or per project in a `.roz.json` file whose keys are the same variable names (section below). An
environment variable always wins over the file. There are no command-line flags beyond the `setup`
subcommand; `roz-mcp setup --tools=<value>` seeds `ROZ_TOOLS` for you. Changes from either source
take effect the next time the client starts the server (reconnect / restart the MCP client).

## Selecting the tool surface (`ROZ_TOOLS`)

`ROZ_TOOLS` chooses which of the server's 19 tools are registered for the session. Shrinking the set
cuts the per-session schema the model has to carry. The value is a list of tokens, **comma- or
semicolon-separated**, matched **case-insensitively** and processed **left-to-right**:

- **Presets** — `all` (every tool), `default` (12 tools: the read and navigate set plus
  `rename_symbol`), `read`, `navigate`, `edit`.
- **Category keys** — `navigation`, `references`, `types`, `editing`, `usings`, `diagnostics`,
  `workspace`. A category token expands to every tool in that category.
- **Tool names** — any individual tool, e.g. `edit_symbol`, `analyze_method`.
- **Exclusions** — a `-` prefix removes the matched preset/category/tool from the set built so far.
  Exclusions subtract from what precedes them, so start from an including token:
  `all,-usings,-edit_symbol` is "everything except the two usings tools and `edit_symbol`".

When `ROZ_TOOLS` is unset (or blank) the **`default`** preset applies. It registers 12 of the 19 tools
and deliberately holds back the seven that either mutate code or are niche:

- The six write tools — `edit_symbol`, `replace_content`, `apply_code_fix`, `change_signature`,
  `add_usings`, `remove_unused_usings`.
- The niche `get_unused_references`.

To get the write tools, opt in explicitly — e.g. `ROZ_TOOLS=default,edit_symbol,rename_symbol` or the
whole `edit` preset, or `all` for everything. Seed it during onboarding with
`roz-mcp setup --tools=all` (or any token list).

Unknown tokens are **dropped with a stderr warning** as long as at least one token in the value is
valid. If **no** token is valid the server refuses to start (a deliberate config crash) — see
Troubleshooting. Recognised tokens that net to an empty set (exclusion-only input) start the server
with **no** tools and log a warning.

## Project config file (`.roz.json`)

A `.roz.json` at the project root sets the same variables per project. It exists for launchers whose
command is configured globally rather than per project (for example, a Claude Code plugin): a global
launch command carries no per-project `env` block, and the file fills that gap for every client.

```json
{
  "ROZ_TOOLS": "default,edit_symbol",
  "ROZ_LOG_LEVEL": "Information"
}
```

Rules:

- Keys are the exact `ROZ_*` variable names from the reference below. An environment variable
  always wins; the file only fills variables that are unset or blank.
- At startup the server walks up from its working directory and uses the first directory that
  contains a `.roz.json`. A nested project can commit `{}` to opt out of a parent's file.
- Only `ROZ_`-prefixed variables from the reference are honored. Unknown keys are skipped with a
  warning, so the file cannot inject arbitrary environment (`PATH`, `DOTNET_*`, …).
- Values are strings; numbers and booleans are accepted and converted (`true` → `"true"`).
- A relative `ROZ_SOLUTION_PATH` is resolved against the directory containing the file.
- Comments and trailing commas are allowed.
- An unparseable file is ignored with a startup warning; it never blocks the server.
- There is no live reload: like env vars, changes apply on the next reconnect.
- Don't commit `ROZ_VS_INSTALL_PATH`: it names a machine-specific Visual Studio path.

In a plugin install, `roz-mcp setup --tools=<value>` writes `ROZ_TOOLS` into this file for you
(the plugin's global launch command has no per-project `env` block to carry it), preserving any
other keys the file already has.

`get_workspace_info` reports the discovered file and the keys it applied, and `roz-mcp setup`
reports the same in its environment checks.

## Environment variable reference

Every variable the server reads. Variables not prefixed `ROZ_` are set by the MCP client, not by roz.

| Name | Default | Effect |
|---|---|---|
| `ROZ_TOOLS` | `default` preset (12 tools) | Which tools are registered — grammar above. |
| `ROZ_SOLUTION_PATH` | unset (CWD-walk) | Explicit `.sln`/`.slnx`/`.slnf` path; bypasses discovery. |
| `ROZ_VS_INSTALL_PATH` | unset (vswhere auto-selects) | Force a specific Visual Studio install root for MSBuild. |
| `ROZ_LOG_LEVEL` | `Warning` | Min level for the rolling file log; Serilog or `Microsoft.Extensions.Logging` level names. |
| `ROZ_SESSION_ID` | unset | Overrides the session id used to tag log lines (wins over `CLAUDE_CODE_SESSION_ID`). |
| `CLAUDE_CODE_SESSION_ID` | set by Claude Code | Client-injected session id for log correlation; GUID fallback when unset. |
| `ROZ_IDLE_TIMEOUT_MINUTES` | `30` (`0` disables) | Self-exit after this many minutes of no tool activity (orphan defense). |
| `ROZ_DISABLE_PARENT_WATCH` | `false` | Disable the Windows parent-process death watch. |
| `ROZ_DISABLE_AUTO_REFRESH` | `false` | Disable external-edit reconciliation (FileSystemWatcher + entry-time mtime sweep). |
| `ROZ_DISABLE_ANALYZERS` | `false` | Skip analyzer execution in `get_diagnostics` (compiler diagnostics still run). |
| `ROZ_TEST_PATHS` | empty | Extra comma/semicolon path prefixes that classify a project as a test project. |
| `ROZ_TEST_NAMESPACES` | empty | Extra comma/semicolon namespace prefixes that classify a project as a test project. |
| `ROZ_MAX_RESPONSE_CHARS` | `25,000` | Hard cap on a single tool response before truncation. |
| `MAX_MCP_OUTPUT_TOKENS` | set by MCP client | Fallback char cap (× 2.5) when `ROZ_MAX_RESPONSE_CHARS` is unset. |

## Troubleshooting

**A tool is missing from the registered set.** The `default` preset excludes the write tools and
`get_unused_references` (list above). Add the tool name — or a preset/category that includes it —
to `ROZ_TOOLS`, then reconnect the client. Example:
`ROZ_TOOLS=default,change_signature`.

**The server won't start / "resolved to zero valid tool tokens".** Every token in `ROZ_TOOLS` was
unrecognised, so startup throws. The message lists the valid keys. Fix the value (check for typos in
presets, categories, and tool names) and reconnect. This error fires before the server is usable, so
the fix is edit-and-restart, not a tool call.

**The solution isn't found, or the wrong one loads.** Discovery order is: `ROZ_SOLUTION_PATH` if set →
a single `.sln`/`.slnx`/`.slnf` in the working directory → a walk up parent directories for one. Set
`ROZ_SOLUTION_PATH` to the exact file when the working directory has zero or several candidates.

**A setting isn't taking effect, or an error names a variable you never set in the client config.**
A `.roz.json` up the directory tree may be supplying it, or a real environment variable may be
overriding your file (the environment variable always wins). Three surfaces show the provenance: the
server log records what the file applied at startup, `get_workspace_info` prints a `Config file:`
line with the applied keys, and `roz-mcp setup`'s environment checks report the file, its applied
keys, and any keys the environment overrode.

**Startup crashes with an MSBuild / `XMakeElements` type-init error.** This happens when a Visual
Studio *preview* install (e.g. VS 18) is auto-selected and the solution has legacy non-SDK projects.
Point `ROZ_VS_INSTALL_PATH` at a stable VS install root (16/17) to override the auto-selection.

**An analyzer pack misbehaves** (crashes or floods `get_diagnostics`). Set `ROZ_DISABLE_ANALYZERS=true`
to run compiler diagnostics only. Note this also means analyzer-owned diagnostic IDs report nothing to
fix via `apply_code_fix`.

**Responses are being cut off.** Output is truncated at the last line boundary once it exceeds the cap.
The cap resolves as `ROZ_MAX_RESPONSE_CHARS` (a positive integer) → else `MAX_MCP_OUTPUT_TOKENS × 2.5`
→ else 25,000 characters. Raise `ROZ_MAX_RESPONSE_CHARS` if your client can accept larger tool results.
