# Contributing to roz-mcp

roz-mcp is a Roslyn-powered MCP server for C#: a .NET global tool that gives coding
agents semantic navigation and conservative, verified code edits. Thanks for your
interest in working on it. For what it does and how to install it, see the
[README](README.md); this document is about working on the code.

## What contributions land well here

- Bug reports reproduced on a public or open-source codebase. The whole project is
  built around running against real, large solutions (see the stress suite below), so a
  repro on something we can all clone is the most actionable thing you can file.
- New conservative write-path tools or executors. The editing tools are strict:
  name-authoritative resolution, verified writes, refusal on any unsafe site.
  New tools in that mold (or new executors behind the existing analysis) fit well.
- MCP client compatibility fixes. The server targets Claude Code, Cursor, VS Code
  Copilot Chat, and Codex CLI; fixes for how any of those launch, configure, or talk to
  it are welcome.
- Eval-harness and evidence improvements: better A/B tasks, verifiers, or measurement
  fidelity in `scripts/ab-test/`, and better write-ups under `docs/evidence/`.

## Development setup

- **.NET 10 SDK** is required. The solution file is `Zphil.Roz.slnx` (`.slnx`, not `.sln`),
  which needs a current SDK to load.
- **Windows or Linux.** On Windows the server locates MSBuild via `vswhere.exe`; on Linux it
  uses the SDK's bundled MSBuild.

Build the whole solution:

```bash
dotnet build Zphil.Roz.slnx
```

## Running tests

```bash
dotnet test tests/Zphil.Roz.Tests/Zphil.Roz.Tests.csproj

# A single test by fully-qualified name
dotnet test tests/Zphil.Roz.Tests/Zphil.Roz.Tests.csproj --filter "FullyQualifiedName~FindSymbol_ByExactName_FindsInterface"
```

The test project splits into two workspace patterns, worth understanding before you add
a test:

- **Read-only tests** inject the shared assembly-level workspace fixtures
  (`WorkspaceFixture` / `DiagnosticWorkspaceFixture`) directly via the constructor. These
  load the checked-in `TestFixture.sln` once and share it across every read-only test. Use
  them for navigation, references, type hierarchy, and diagnostics: anything that does not
  mutate the workspace.
- **Edit tests** extend `EditTestBase`, which uses `EditWorkspaceFixture`. That fixture
  loads `EditFixture.slnf`, a solution filter over a subset of the fixture projects, not
  the full `TestFixture.sln`. Between tests it restores file bytes (and fully reloads when
  files were renamed or deleted), so it is safe for rename tests too. If a new edit test
  needs cross-project resolution into a fixture project the filter does not yet include,
  add that project to the `.slnf`.
- **`TempWorkspaceFactory`** is reserved for tests that exercise the `FileSystemWatcher` or
  the `WorkspaceManager` lifecycle (manual dispose/recreate). Use the shared fixtures for
  everything else, including renames.

Fixture source files live under `tests/Zphil.Roz.Tests/Fixtures/` and are excluded from
compilation: they are copied to the output directory as content.

## Stress tests

```bash
dotnet test tests/Zphil.Roz.StressTests/Zphil.Roz.StressTests.csproj
```

The stress suite is Windows-only. On first run it clones a pinned copy of nopCommerce
(~35 projects, ~300k LOC) into `%LOCALAPPDATA%` and caches it; later runs reuse the cache.
The suite is slow and does not run in CI; no PR requires it. Run it when your
change could affect behavior or performance at scale.

## Pull request expectations

- Small and focused: one change per PR.
- A green build and passing unit tests (see [Running tests](#running-tests)).
- XML doc comments on public members.
- Evidence first: changes to tool output, routing text, or default behavior need a
  stated rationale in the PR. A change to the default tool preset needs an A/B run to
  back it (see the harness below). HOLD verdicts are normal here: two of our own tools
  failed their promotion A/Bs, and one of them (`analyze_method`) is still held out of the
  default preset because of it.

### Running the A/B harness

The A/B harness lives at [`scripts/ab-test/`](scripts/ab-test/) (see its
[README](scripts/ab-test/README.md)). It is a Python harness that runs paired agent
sessions (one arm with roz-mcp, one without) against the same cached nopCommerce clone
and measures token cost, wall-clock, tool usage, and end-state diffs. It is Windows-only
and needs Claude Code access (the `claude-agent-sdk`). Run output under `results/` is
gitignored; the canonical write-ups are dated markdown docs under
[`docs/evidence/`](docs/evidence/).

## Versioning and releases

The project follows semantic versioning from 1.0.0. Breaking changes to the tool surface bump the major version.
Release notes live on [GitHub Releases](https://github.com/andypgray/roz-mcp/releases);
there is no `CHANGELOG` file by design. A maintainer tags `v*` and CI publishes the package
to NuGet.

## License

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE),
the same license as the project.
