# Privacy

This policy covers roz: the MCP server published on nuget.org as `Zphil.Roz` and the Claude Code plugin distributed from this repository. Effective 2026-07-19; changes are tracked in this file's git history.

## What roz collects

Nothing. roz has no telemetry, no analytics, no accounts, and no remote logging. The author receives no data from your use of it.

## How your source code is processed

Everything runs on your machine. roz loads your solution with MSBuild and Roslyn in the server process your MCP client launches (design-time builds run in an MSBuild BuildHost subprocess, also local). The results returned over stdio to that client (symbols, references, diagnostics, edit results) are derived from your code and go nowhere else. This project operates no servers and never sees your code or the results.

## What your MCP client does with the results

Tool output becomes part of your agent conversation. How the MCP client (for example, Claude Code) stores or transmits that conversation is governed by that client's own privacy policy, not this one.

## Local logs

Diagnostic logs roll daily under `%LOCALAPPDATA%\Zphil.Roz\logs` on Windows, and the platform-equivalent path elsewhere, keeping 7 daily files. They can contain absolute paths and symbol names read from your solution. They stay on the machine; delete them whenever you like. `ROZ_LOG_LEVEL` controls how much is written.

## Network access

roz makes no network calls: no listener, no outbound requests. Installation is the one network step around it: `dotnet tool install -g Zphil.Roz` and, on .NET 10, `dnx Zphil.Roz` download the package from nuget.org, a Microsoft service with [its own privacy statement](https://go.microsoft.com/fwlink/?LinkId=521839).

## Contact

Questions about this policy: open an issue on [andypgray/roz-mcp](https://github.com/andypgray/roz-mcp/issues), or email andypgray@protonmail.com.
