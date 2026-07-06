> **Historical record.** Written 2026-04-06 against the tool as it existed then. The server was
> named `roslyn-mcp` before the 2026-07 rename to `roz-mcp`, so the tool names, tool counts, and
> preset sizes in the text below are period-correct for that date, not the current tool surface.
> Published July 2026 verbatim except for the redaction of internal file paths. A few links to
> internal working files (`../backlog/`, run-output `results/`, pre-rename source paths) do not
> resolve in this repository and are left as written rather than rewritten. Bugs identified
> here were subsequently fixed. See the evidence index at [README.md](README.md) for how this
> document maps to the project's current claims.

# Wolverine Stress Test Results (2026-04-06)

## Overview

Stress-tested the Roslyn MCP server against [JasperFx/wolverine](https://github.com/JasperFx/wolverine), a .NET distributed application framework. Wolverine was chosen to validate cross-project navigation, source-generated handler dispatch, convention-based handler discovery, and DI registration detection at scale.

**Solution**: `wolverine.sln` — 138 projects, 8,155 documents, multi-TFM (net8.0/net9.0/net10.0)

**Method**: 72 tool calls across 10 categories, covering all 19 Roslyn MCP tools. All edits reverted via `git checkout` after testing.

**Result**: 59 pass, 13 fail/issue. 4 bugs identified (2 MEDIUM, 2 LOW).

---

## Bugs

### BUG-1 (MEDIUM): `remove_unused_usings` removes required usings when type resolution is incomplete

#### Summary

In a multi-TFM solution where NuGet/framework dependencies aren't fully resolved, `remove_unused_usings` incorrectly identifies actively-used `using` directives as unused and removes them. This happens because the types from those namespaces are already unresolvable (CS0246 at baseline), making their usings appear unused.

#### Root Cause

The Roslyn workspace has 126K+ baseline diagnostics (mostly CS0518 and CS0246) because multi-TFM resolution doesn't fully load all framework/NuGet assemblies. When a type like `IDocumentSession` (from `Marten`) can't be resolved, the `using Marten;` directive appears unused to Roslyn's analysis.

#### Reproduction Steps

**File 1: `src/Samples/DocumentationSamples/HandlerExamples.cs`**

1. Confirm the file has 6 usings, all actively used:

   ```
   using Marten;                        // IDocumentSession (lines 22, 29, 165, 167, 192)
   using Microsoft.Extensions.Hosting;  // Host.CreateDefaultBuilder() (line 229)
   using Wolverine.ComplianceTests;     // Message1, Message2, Message3 (many lines)
   using Wolverine.ComplianceTests.Compliance;
   using Wolverine;                     // Envelope (line 208), IMessageBus (line 107)
   using Wolverine.Attributes;          // [WolverineHandler] (line 12), [WolverineIgnore] (lines 159, 186)
   ```

2. Run `remove_unused_usings`:

   ```json
   { "filePaths": ["src/Samples/DocumentationSamples/HandlerExamples.cs"] }
   ```

3. **Result**: Removes 4 usings: `Wolverine`, `Marten`, `Wolverine.Attributes`, `Microsoft.Extensions.Hosting`

4. The file now has only 2 usings and `[WolverineHandler]`, `IDocumentSession`, `Envelope`, `Host.CreateDefaultBuilder()` are all unresolvable.

5. Running `get_diagnostics` with `incremental: true` shows **0 new errors** because those types were already broken in baseline.

**File 2: `src/Wolverine/Runtime/WolverineRuntime.cs`** (core framework file)

1. Confirm the file has 15 usings. Check baseline diagnostics:

   ```json
   {
     "filePaths": ["src/Wolverine/Runtime/WolverineRuntime.cs"],
     "severity": "Error",
     "diagnosticIds": ["CS0246"]
   }
   ```

   Returns 15 CS0246 errors for types like `ILogger`, `ObjectPool<>`, `ImHashMap<,>`, `IHostedService`, `IServiceContainer`.

2. Run `remove_unused_usings`:

   ```json
   { "filePaths": ["src/Wolverine/Runtime/WolverineRuntime.cs"] }
   ```

3. **Result**: Removes 8 usings: `JasperFx`, `Microsoft.Extensions.Logging`, `JasperFx.Core.Reflection`, `Microsoft.Extensions.ObjectPool`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.DependencyInjection`, `ImTools`, `JasperFx.Core`

4. Only `System.Diagnostics.Metrics` and internal `Wolverine.*` usings remain. The file is now catastrophically broken.

#### Impact

- **Silent data loss**: No warning is given. The tool reports success.
- **Incremental diagnostics don't catch it**: Since the types were already unresolvable, removing usings introduces 0 new diagnostics.
- **Vicious cycle**: Removing usings makes it impossible to resolve those types even if the underlying resolution issue is later fixed.

#### Suggested Fix

Before removing a using, check if the `using` line itself has a CS0246 diagnostic. If a namespace can't be resolved, the using should be preserved — it may be "unused" only because resolution failed. Alternatively, warn the user: "N unresolved namespaces in this file; some usings may be incorrectly flagged as unused."

---

### BUG-2 (MEDIUM): `get_diagnostics` race condition with `get_workspace_info` on cold start

#### Summary

When `get_workspace_info` and `get_diagnostics` are called in parallel immediately after workspace load, `get_workspace_info` succeeds but `get_diagnostics` fails with "Solution directory is not available — the solution has not been loaded yet."

#### Reproduction Steps

1. Start a fresh Roslyn MCP session (cold start).

2. Call both tools in parallel:

   ```json
   // Call 1
   { "tool": "get_workspace_info" }
   // Call 2 (parallel)
   { "tool": "get_diagnostics" }
   ```

3. **Expected**: Both succeed.

4. **Actual**: `get_workspace_info` returns 138 projects / 8,155 documents. `get_diagnostics` returns error: "Solution directory is not available — the solution has not been loaded yet."

5. Retrying `get_diagnostics` immediately after succeeds.

#### Impact

Agents that call both tools in parallel on startup will get an error from `get_diagnostics`. Workaround: call `get_workspace_info` first, then `get_diagnostics`.

#### Notes

Not reproducible in warm state. Requires a cold Roslyn server start with a large solution.

---

### BUG-3 (LOW): `find_callers` does not detect `IServiceCollection.AddSingleton<T>()` DI registrations for constructors

#### Summary

`find_callers` on a constructor (`.ctor`) correctly finds direct `new` instantiations but does not detect `IServiceCollection.AddSingleton<T>()` or `AddSingleton<TService, TImpl>()` DI registrations, despite the response message mentioning "or DI registrations."

#### Reproduction Steps

**Control — direct `new` call (works)**:

1. Confirm `NullMessageStore` is instantiated directly:

   ```
   // src/Wolverine/Persistence/MessageStoreCollection.cs:146
   Main = new NullMessageStore();
   // src/Wolverine/Persistence/MessageStoreCollection.cs:149
   public IMessageStore Main { get; private set; } = new NullMessageStore();
   ```

2. Run `find_callers`:

   ```json
   { "symbolName": ".ctor", "containingType": "NullMessageStore", "maxResults": 10 }
   ```

3. **Result**: 2 callers found at `MessageStoreCollection.cs:146` and `:149`. Correct.

**Test case 1 — `AddSingleton<TService, TImpl>()` (fails)**:

1. Confirm the DI registration exists:

   ```
   // src/Wolverine/HostBuilderExtensions.cs:171
   services.AddSingleton<IWolverineRuntime, WolverineRuntime>();
   ```

2. Run `find_callers`:

   ```json
   { "symbolName": ".ctor", "containingType": "WolverineRuntime", "includeTests": true, "maxResults": 15 }
   ```

3. **Result**: "No direct callers or DI registrations found for '.ctor'."

**Test case 2 — `AddSingleton<T>()` (also fails)**:

1. Confirm:

   ```
   // src/Wolverine/HostBuilderExtensions.cs:183
   services.AddSingleton<InMemorySagaPersistor>();
   ```

2. Run `find_callers`:

   ```json
   { "symbolName": ".ctor", "containingType": "InMemorySagaPersistor", "includeTests": true, "maxResults": 10 }
   ```

3. **Result**: "No direct callers or DI registrations found for '.ctor'."

#### Impact

The "or DI registrations" in the response message implies DI detection should work, but it doesn't for the standard `IServiceCollection` extension methods. Types exclusively registered via DI (never `new`'d directly) will always show 0 callers.

#### Notes

This may be related to BUG-1's root cause — the `Microsoft.Extensions.DependencyInjection` namespace may not be fully resolved, preventing the DI recognizer from matching `AddSingleton` as an `IServiceCollection` extension method. If the DI recognizer requires resolved method symbols to match, unresolved extension methods would be invisible to it.

---

### BUG-4 (LOW): `find_implementations` returns nothing for marker interfaces (no guidance to use `find_derived_types`)

#### Summary

`find_implementations` on a marker interface (one with zero members) returns "No implementations found" with no suggestion to use `find_derived_types` instead. Since marker interfaces have no members to implement, `find_implementations` will always return empty — but types do implement the interface.

#### Reproduction Steps

1. Confirm `IWolverineHandler` is a marker interface:

   ```json
   {
     "names": ["IWolverineHandler"],
     "kind": "Interface",
     "depth": 1,
     "includeTests": true
   }
   ```

   Result: 1 interface found, 0 members listed.

2. Run `find_implementations`:

   ```json
   { "symbolName": "IWolverineHandler", "kind": "Interface", "includeTests": true, "maxResults": 20 }
   ```

   **Result**: "No implementations found for 'IWolverineHandler'."

3. Run `find_derived_types` on the same interface:

   ```json
   { "symbolName": "IWolverineHandler", "kind": "Interface", "includeTests": true, "maxResults": 20 }
   ```

   **Result**: 9 types found across 6 projects, including `ConsumerOne`, `ConsumerTwo`, `MessageHandlers`, `IConsumer<T>`, `IRequestHandler<TRequest, TResponse>`, etc.

#### Impact

Users asking "what implements this interface?" will get 0 results for marker interfaces, with no guidance on what to do instead. The `find_implementations` response should suggest `find_derived_types` when the target interface has no members.

---

## What Works Well

### Cross-Project Navigation at Scale

| Query | Results |
|-------|---------|
| `find_references` on `WolverineOptions` | 252 refs across 86 files, 21 projects |
| `find_derived_types` on `MessageHandler` | 89 types across 8 projects |
| `find_implementations` on `ITransport` | 21 implementations across 14 projects |
| `find_implementations` on `IMessageStore` | 10 implementations (all DB providers) |
| `find_callers` on `GetOrCreate<T>` | 34 callers across 7 transport projects |
| `find_derived_types` on `Saga` | 74 types across 19 projects |
| `find_derived_types` on `BrokerTransport` | 7 types (AmazonSqs, Azure, Kafka, Nats, Pubsub, RabbitMQ, Redis) |

### Generic Type Hierarchies

Deep cross-project generic hierarchies resolve correctly:

```
RabbitMqTransport -> BrokerTransport<RabbitMqEndpoint> -> TransportBase<RabbitMqEndpoint>
  implements: IAsyncDisposable, IBrokerTransport, ITagged, ITransport
```

Same pattern verified for `KafkaTransport -> BrokerTransport<KafkaTopic> -> TransportBase<KafkaTopic>`.

### Wildcard Symbol Search

`find_symbol` with `*Handler` glob found 276 classes across 56 projects.

### Convention Method Discovery

| Convention | Matches | Projects |
|-----------|---------|----------|
| `Starts` (saga start) | 55 | 19 |
| `Consume` (handler) | 44 | 8 |
| `OnException` (middleware) | 25 | 4 |
| `BeforeAsync` (middleware) | 13 | varies |

### Solution-Wide Rename

`rename_symbol` of `HttpMessage1 -> HttpMessageAlpha` propagated correctly across 8 files in 2 projects (WolverineWebApi + Wolverine.Http.Tests).

### Large File Overviews

| File | Members | Result |
|------|---------|--------|
| `MultiTenantedMessageStore.cs` | 86 | Clean overview with explicit interface implementations |
| `HandlerGraph.cs` | 51 | All fields, properties, methods listed |
| `Envelope.cs` | 64 | All 3 constructors and 64 members |
| `PolicyExpression.cs` | 8 types, ~70 members total | Nested classes and interfaces shown |

### Edit Operations

| Operation | File | Result |
|-----------|------|--------|
| `insert_symbol` | `MessageHandlers.cs` | Inserted method after existing `Handle` overload |
| `replace_symbol` | `HandlerExamples.cs` | Replaced method body, 4 -> 4 lines |
| `rename_symbol` | `MessageHandlers.cs` | Propagated across 8 files |
| `add_usings` | `HandlerExamples.cs` | Added and sorted correctly |
| `replace_content` | `GET_api_test.cs` | Edited `[GeneratedCode]` file successfully |
| `remove_symbol` | `MessageHandlers.cs` | Removed specific overload cleanly |

### Negative/Boundary Searches

- `find_symbol("ServiceRegistry", kind: Class)` — 0 results (correct: Wolverine uses MS DI, not Lamar)
- `find_symbol("ConfigureLamar")` — 0 results with "did you mean?" suggestions
- Both confirm the Lamar DI recognizer boundary correctly

### Incremental Diagnostics

After edit operations, `get_diagnostics(incremental: true)` correctly reported the delta from baseline (5 new metadata-level, 18K resolved, 0 new source-level).

### Partial Class Support

`AzureServiceBusTransport` shown with 3 source locations in `find_implementations`; `MultiTenantedMessageStore` and `CosmosDbMessageStore` shown with multiple partial file locations.

---

## Tool Coverage

| Tool | Calls | Issues |
|------|-------|--------|
| `find_callers` | 11 | DI registration not detected (BUG-3) |
| `find_references` | 9 | All passed |
| `get_symbols_overview` | 10 | 2 "file not found" for projects not in solution |
| `find_symbol` | 9 | All passed |
| `find_implementations` | 6 | Marker interface returns 0 (BUG-4) |
| `get_type_hierarchy` | 6 | All passed |
| `find_derived_types` | 4 | All passed |
| `go_to_definition` | 4 | All passed (1 positional nuance) |
| `find_overloads` | 5 | All passed |
| `get_workspace_info` | 1 | Passed |
| `get_diagnostics` | 3 | Race on cold start (BUG-2) |
| `reset_baseline` | 1 | Passed |
| `insert_symbol` | 2 | 1 retry needed (line on whitespace) |
| `rename_symbol` | 1 | Passed — 8-file propagation |
| `replace_symbol` | 2 | 1 retry needed (line on whitespace) |
| `add_usings` | 1 | Passed |
| `remove_unused_usings` | 2 | Removes required usings (BUG-1) |
| `remove_symbol` | 2 | 1 retry needed (stale line after insert) |
| `replace_content` | 1 | Passed |

---

## Test Environment

- **Wolverine version**: 5.13.0
- **Solution**: `wolverine.sln` — 138 projects, 8,155 documents
- **Target frameworks**: net8.0, net9.0, net10.0 (multi-TFM)
- **Baseline diagnostics**: 126,307 (mostly CS0518/CS0246 from TFM resolution)
- **Platform**: Windows 10 Pro, .NET SDK present
