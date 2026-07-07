# Security

This document covers reporting a vulnerability in roz, the server's threat model, and verifying the package you install.

## Supported versions

Only the latest release on nuget.org receives security fixes.

## Reporting a vulnerability

Report privately through [GitHub security advisories](https://github.com/andypgray/roz-mcp/security/advisories/new). Do not open a public issue for a security report. You can expect an acknowledgment within 7 days.

## Security model

roz is a local process, and its threat model follows from that:

- roz runs as a stdio subprocess launched by your MCP client, with your user privileges. It opens no network listener, makes no outbound calls, and sends no telemetry.
- Loading a solution runs MSBuild design-time builds, which execute the MSBuild logic (props and targets) shipped in that solution, inside a BuildHost subprocess. Open an untrusted solution with the same caution you would apply to building it.
- The write surface is 7 of the 19 tools. The default tool set excludes 6 of them, and the Claude Code setup routes all 7 to permission prompts (the `ask` list in `.claude/settings.local.json`), so edits need your confirmation.
- Logs under `%LOCALAPPDATA%\Zphil.Roz\logs` can contain absolute paths and symbol names read from your solution. The sink keeps 7 daily rolling files, and nothing leaves the machine.

## Verify what you install

Releases after v1.0.0 ship a signed build provenance attestation. (v1.0.0 predates this change, so its assets are not attested.) Download the `.nupkg` from the GitHub release and verify it as built:

```bash
gh attestation verify Zphil.Roz.<version>.nupkg --repo andypgray/roz-mcp
```

The release also carries the Sigstore bundle (`attestation.intoto.jsonl`) for offline verification with `gh attestation verify --bundle`.

The nuget.org copy differs. nuget.org appends a repository signature (`.signature.p7s`) after upload, which changes the file hash, so the attestation matches the GitHub release copy rather than the file you download from nuget.org. Use the GitHub release asset for digest verification, and `dotnet nuget verify <file>` for the nuget.org repository signature.

`dotnet tool install -g Zphil.Roz` and, on .NET 10, `dnx Zphil.Roz` install the same nuget.org package. `roz-mcp --version` prints `<version>+<commit>`, and that commit matches the release tag on andypgray/roz-mcp, a source cross-check that needs no tooling.

## Supply chain

Publishing uses NuGet trusted publishing (OIDC), so there is no long-lived API key to store or leak. The public release workflow builds, tests, packs, and attests every package. Builds use SourceLink and a deterministic CI configuration, and every GitHub Actions dependency is pinned to a commit SHA, with Dependabot keeping the pins current.
