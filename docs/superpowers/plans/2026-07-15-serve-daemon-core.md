# Serve Daemon Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete issue #188 with a line-delimited JSON daemon that serially routes all required commands through one identity-verified, daemon-owned ETABS session and safely removes managed orphans.

**Architecture:** Keep the existing one-shot command lifecycle intact while extracting caller-owned `ETABSApplication` paths for serve mode. Put process identity, durable record I/O, orphan cleanup, and ETABS launch/cleanup behind focused interfaces; `EtabsSession` composes them and verifies the full identity tuple before returning its cached application. `ServeLoop` remains the serialization boundary and emits the version-1 handshake before processing requests.

**Tech Stack:** C# 14 / .NET 10, System.CommandLine, Microsoft.Extensions.DependencyInjection, System.Text.Json, System.Diagnostics, EtabSharp 0.3.5-beta, xUnit v3.

---

## File Map

Create:

- `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ManagedEtabsSessionRecord.cs` — versioned record and identity value types.
- `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ISessionRecordStore.cs` — durable store contract.
- `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/JsonSessionRecordStore.cs` — atomic `%LOCALAPPDATA%` JSON persistence.
- `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/IProcessInspector.cs` — process snapshots, tuple lookup, termination, and exit waiting.
- `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/WindowsProcessInspector.cs` — `System.Diagnostics.Process` implementation.
- `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/IManagedEtabsApplication.cs` — fakeable owned-wrapper boundary.
- `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ManagedEtabsApplication.cs` — EtabSharp wrapper lifecycle adapter.
- `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/IManagedEtabsLauncher.cs` — launch contract.
- `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ManagedEtabsLauncher.cs` — deterministic before/after process capture and idempotent hide.
- `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/IOrphanSessionCleaner.cs` — startup cleanup contract.
- `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/OrphanSessionCleaner.cs` — full-tuple orphan decision logic.
- `src/EtabExtension.CLI/Features/ReadModelMetadata/IReadModelMetadataService.cs` — shared-session metadata contract.
- `src/EtabExtension.CLI/Features/ReadModelMetadata/ReadModelMetadataService.cs` — open/read metadata operation.
- `EtabExtension.CLI.Tests/ManagedSessionRecordTests.cs` — record persistence tests.
- `EtabExtension.CLI.Tests/OrphanSessionCleanerTests.cs` — orphan decision tests.
- `EtabExtension.CLI.Tests/EtabsSessionTests.cs` — single-launch, verification, and cleanup tests.
- `EtabExtension.CLI.Tests/ServeDispatcherTests.cs` — all command routes using fake feature services/session.

Modify:

- `Features/Serve/ServeProtocol.cs`, `ServeLoop.cs`, `ServeCommand.cs`, `ServeExtensions.cs`, `ServeRequests.cs`, `ServeDispatcher.cs`.
- `Shared/Infrastructure/Etabs/Session/EtabsSession.cs`.
- Service interfaces and implementations for close, unlock, generate E2K, extract results, and extract materials.
- `Program.cs` to register the metadata feature if registration is not contained in `AddServeFeature`.
- Existing serve tests for handshake-aware framing and serialized overlap detection.

### Task 1: Lock the protocol framing and serialization boundary

**Files:**
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeProtocol.cs`
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeLoop.cs`
- Test: `EtabExtension.CLI.Tests/ServeLoopTests.cs`

- [ ] **Step 1: Add failing handshake and strict-serialization tests**

Add a dispatcher that increments an in-flight counter, delays the first request through a `TaskCompletionSource`, and records `MaxInFlight`. Assert the first output line is exactly:

```json
{"protocol":"etab-cli-serve","version":1}
```

Then assert response IDs remain `1, 2` and `MaxInFlight == 1`. Update the existing response helper to parse the first line separately instead of treating it as a correlated response.

- [ ] **Step 2: Run the focused tests and confirm the handshake test fails**

Run:

```powershell
dotnet test EtabExtension.CLI.Tests\EtabExtension.CLI.Tests.csproj --filter FullyQualifiedName~ServeLoopTests
```

Expected: the handshake assertion fails because `ServeLoop` currently writes only correlated responses.

- [ ] **Step 3: Add a typed handshake and emit it before reading stdin**

Add to `ServeProtocol.cs`:

```csharp
public sealed record ServeHandshake(
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("version")] int Version);
```

At the beginning of `RunAsync`, call a compact line writer with `new ServeHandshake("etab-cli-serve", 1)`. Continue awaiting each `_dispatcher.DispatchAsync` and `WriteAsync` before the next `ReadLineAsync`; do not introduce `Task.Run`, channels, or background command execution.

- [ ] **Step 4: Run the focused tests**

Expected: handshake, correlation, malformed JSON, dispatcher failure, shutdown, and `MaxInFlight == 1` all pass.

### Task 2: Implement the durable identity record

**Files:**
- Create: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ManagedEtabsSessionRecord.cs`
- Create: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ISessionRecordStore.cs`
- Create: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/JsonSessionRecordStore.cs`
- Test: `EtabExtension.CLI.Tests/ManagedSessionRecordTests.cs`

- [ ] **Step 1: Add failing round-trip, invalid-record, and clear tests**

Use a temporary directory and construct the store with an explicit test path. Round-trip this value:

```csharp
new ManagedEtabsSessionRecord(
    SchemaVersion: 1,
    Pid: 1234,
    ProcessStartTimeUtc: new DateTimeOffset(2026, 7, 15, 1, 2, 3, TimeSpan.Zero),
    ExecutablePath: @"C:\Program Files\CSI\ETABS.exe",
    ManagedLaunchRecordId: Guid.Parse("b956bd77-7254-44f6-bab4-d7381ae1187d"),
    CreatedAtUtc: new DateTimeOffset(2026, 7, 15, 1, 2, 4, TimeSpan.Zero));
```

Assert all fields survive, malformed JSON returns a diagnostic result rather than throwing, and `ClearAsync` removes the file.

- [ ] **Step 2: Run the focused tests and confirm missing types fail compilation**

- [ ] **Step 3: Implement versioned records and atomic JSON writes**

Define:

```csharp
public sealed record ManagedProcessIdentity(int Pid, DateTimeOffset ProcessStartTimeUtc, string ExecutablePath);
public sealed record ManagedEtabsSessionRecord(
    int SchemaVersion,
    int Pid,
    DateTimeOffset ProcessStartTimeUtc,
    string ExecutablePath,
    Guid ManagedLaunchRecordId,
    DateTimeOffset CreatedAtUtc);
```

`JsonSessionRecordStore.DefaultPath` must combine `Environment.SpecialFolder.LocalApplicationData`, `EtabExtension`, `sidecar`, and `managed-etabs-session.json`. Write to `<path>.tmp`, flush/close it, then use `File.Move(temp, path, true)`. Normalize stored paths with `Path.GetFullPath`.

- [ ] **Step 4: Run the focused tests and inspect the serialized property names**

Expected camelCase keys: `schemaVersion`, `pid`, `processStartTimeUtc`, `executablePath`, `managedLaunchRecordId`, `createdAtUtc`.

### Task 3: Implement full-tuple orphan decisions

**Files:**
- Create: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/IProcessInspector.cs`
- Create: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/WindowsProcessInspector.cs`
- Create: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/IOrphanSessionCleaner.cs`
- Create: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/OrphanSessionCleaner.cs`
- Test: `EtabExtension.CLI.Tests/OrphanSessionCleanerTests.cs`

- [ ] **Step 1: Add table-driven failing tests**

Cover: absent record, corrupt record, dead PID, matching PID with different start time, matching PID/start with different path, and full match. Assert only the full match calls `TerminateAsync(pid)` and `WaitForExitAsync(pid)`. Assert every stale/handled record is cleared and a full match reports `CleanReopenRequired == true`.

- [ ] **Step 2: Run the focused tests and confirm missing implementations fail compilation**

- [ ] **Step 3: Implement the inspector and cleaner**

Use ordinal-ignore-case comparison on normalized full paths and exact UTC instant comparison for start time. `WindowsProcessInspector` must catch `ArgumentException`, `InvalidOperationException`, and `System.ComponentModel.Win32Exception` and return an unavailable-process result instead of throwing. Termination uses `Process.Kill(entireProcessTree: true)` only after the cleaner has matched PID, start time, and executable path.

The cleaner must never call an ETABS API, attach to a process, save a model, or adopt a COM object.

- [ ] **Step 4: Run the focused tests**

Expected: full match terminates once; all partial matches terminate zero times.

### Task 4: Replace the concrete session lifecycle with a fakeable owned wrapper

**Files:**
- Create: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/IManagedEtabsApplication.cs`
- Create: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ManagedEtabsApplication.cs`
- Create: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/IManagedEtabsLauncher.cs`
- Create: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ManagedEtabsLauncher.cs`
- Modify: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/EtabsSession.cs`
- Test: `EtabExtension.CLI.Tests/EtabsSessionTests.cs`

- [ ] **Step 1: Add failing fake-wrapper tests**

The fake launcher returns the same fake `IManagedEtabsApplication` handle with an identity and launch-record UUID. Assert two `GetOrStartOwnedAsync` calls launch once, read/verify the durable record twice, and return the same handle. Add mismatch tests for PID, start time, executable path, and launch-record UUID. Assert mismatch returns/throws a clean-reopen failure before exposing the handle. Assert shutdown calls `ApplicationExit(false)`, `Dispose`, and record clear once even when shutdown is called twice.

- [ ] **Step 2: Run the focused tests and confirm failure**

- [ ] **Step 3: Implement the wrapper and deterministic launcher**

The owned boundary exposes:

```csharp
public interface IManagedEtabsApplication : IDisposable
{
    ETABSApplication Application { get; }
    ManagedProcessIdentity Identity { get; }
    Guid ManagedLaunchRecordId { get; }
    bool IsVisible { get; }
    void Hide();
    void ExitWithoutSaving();
}
```

`ManagedEtabsLauncher` snapshots ETABS process identities before `ETABSWrapper.CreateNew()`, launches, then polls for identities absent from the before-snapshot. It succeeds only when exactly one new identity is attributable to the launch. If zero or multiple candidates remain at the deadline, it exits/disposes the newly created COM application and returns a launch error; it must not pick `FirstOrDefault`. Check `Application.Visible()` before `Hide()`.

`IEtabsSession.GetOrStartOwnedAsync` validates the live OS identity and durable record before returning the cached handle. On first launch it creates a UUID, persists the record, and then returns the handle. `GetStatus` can obtain the verified PID from the handle.

- [ ] **Step 4: Run the focused tests**

Expected: one launch, full-tuple checks, and idempotent cleanup all pass without loading ETABS.

### Task 5: Extract caller-owned operations from remaining services

**Files:**
- Modify: `Features/CloseModel/ICloseModelService.cs`, `CloseModelService.cs`
- Modify: `Features/UnlockModel/IUnlockModelService.cs`, `UnlockModelService.cs`
- Modify: `Features/GenerateE2K/IGenerateE2KService.cs`, `GenerateE2KService.cs`
- Modify: `Features/ExtractResults/IExtractResultsService.cs`, `ExtractResultsService.cs`
- Modify: `Features/ExtractMaterials/IExtractMaterialsService.cs`, `ExtractMaterialsService.cs`
- Create: `Features/ReadModelMetadata/IReadModelMetadataService.cs`, `ReadModelMetadataService.cs`

- [ ] **Step 1: Add interface-level compile tests for shared-session methods**

Require these signatures:

```csharp
Task<Result<CloseModelData>> CloseModelOnAppAsync(ETABSApplication app, bool save);
Task<Result<UnlockModelData>> UnlockModelOnAppAsync(ETABSApplication app, string filePath);
Task<Result<GenerateE2KData>> GenerateE2KOnAppAsync(ETABSApplication app, string input, string output, bool overwrite);
Task<Result<ExtractResultsData>> ExtractOnAppAsync(ETABSApplication app, ExtractResultsRequest request);
Task<Result<ExtractMaterialsData>> ExtractMaterialsOnAppAsync(ETABSApplication app, ExtractMaterialsRequest request);
Task<Result<ModelMetadata>> ReadOnAppAsync(ETABSApplication app, string filePath);
```

- [ ] **Step 2: Extract shared private execution methods**

Keep validation before ETABS access. One-shot methods retain `CreateNew`/`Connect` and their existing `finally` cleanup. Each `OnAppAsync` method calls the shared operation and contains no `CreateNew`, `Connect`, `GetAllRunningInstances`, `ApplicationExit`, or `Dispose`.

For default material extraction, call `ExtractOnAppAsync` rather than the one-shot `ExtractAsync`. For metadata, open `filePath`, read current units through `EtabsUnitService.ReadCurrentAsync`, construct a no-change `UnitSnapshot`, and call `EtabsSessionHelpers.CollectModelMetadataAsync`.

- [ ] **Step 3: Add source-contract tests**

Read the shared-operation method source files and assert the serve-facing methods do not contain lifecycle calls. This protects the single-owner invariant without invoking COM.

- [ ] **Step 4: Run all existing tests**

Expected: existing one-shot contract tests remain green.

### Task 6: Route every required Rust request shape

**Files:**
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeRequests.cs`
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeDispatcher.cs`
- Test: `EtabExtension.CLI.Tests/ServeContractTests.cs`
- Test: `EtabExtension.CLI.Tests/ServeDispatcherTests.cs`

- [ ] **Step 1: Add failing route and payload tests**

Cover all ten commands. Use the exact frozen-client fields:

- `open-model`: `filePath`, `saveOnClose`, `newInstance`.
- `close-model`: `save`.
- `unlock-model`, `read-model-metadata`: `filePath`.
- `generate-e2k`: `filePath`, `outputFile`, `overwrite`.
- extraction commands: flattened `filePath`, `outputDir`, `units`, table fields.

Assert the fake session returns one owned handle and each fake feature receives that handle's application. Assert unsupported operation-envelope commands return `success: false` through the normal dispatcher result.

- [ ] **Step 2: Implement request records and dispatch cases**

Before each command requiring ETABS, await `GetOrStartOwnedAsync`; this is the per-command full-tuple verification point. Ignore `newInstance` in serve mode and always report `OpenedInNewInstance = false`. Pass the verified PID to `GetStatusOnApp`.

- [ ] **Step 3: Run focused serve tests**

Expected: every required command routes and no TODO command list remains.

### Task 7: Wire startup orphan cleanup, handshake, and graceful shutdown

**Files:**
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeCommand.cs`
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeExtensions.cs`
- Modify: `src/EtabExtension.CLI/Program.cs`
- Test: `EtabExtension.CLI.Tests/ServeLoopTests.cs`
- Test: `EtabExtension.CLI.Tests/OrphanSessionCleanerTests.cs`

- [ ] **Step 1: Register production dependencies**

Register the record store, process inspector, launcher, orphan cleaner, owned session, dispatcher, and metadata service with daemon-scope-compatible lifetimes. Do not use global mutable singletons.

- [ ] **Step 2: Run orphan cleanup before emitting the handshake**

`ServeCommand` resolves `IOrphanSessionCleaner`, awaits cleanup, writes diagnostics only to `Console.Error`, and then starts `ServeLoop`. Keep session shutdown in `finally`. A matching orphan is terminated before the daemon announces readiness.

- [ ] **Step 3: Verify stdout ownership**

Run non-live protocol tests using `StringReader`/`StringWriter`; assert every output line parses as one JSON object and all diagnostics captured from stderr are absent from protocol output.

### Task 8: Full verification and commits

**Files:** all changed files.

- [ ] **Step 1: Format and inspect**

Run:

```powershell
dotnet format EtabExtension.CLI.slnx --no-restore
git diff --check
rg -n "TODO\(#188|GetAllRunningInstances\(\)\.FirstOrDefault|app\.Application\.Hide\(\)" src/EtabExtension.CLI/Features/Serve src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session
```

Expected: no #188 TODO, no ambiguous first-instance selection in daemon/session code, and visibility changes occur only behind the idempotent wrapper.

- [ ] **Step 2: Run the required xUnit gate**

```powershell
dotnet test EtabExtension.CLI.Tests\EtabExtension.CLI.Tests.csproj
```

Expected: all tests pass; no ETABS process is launched.

- [ ] **Step 3: Run the required solution build**

```powershell
dotnet build EtabExtension.CLI.slnx
```

Expected: build succeeds with zero errors.

- [ ] **Step 4: Review scope and commit implementation**

Confirm `git status --short` contains only C# repository files and no sibling-repository edits. Commit implementation and tests with a message such as:

```powershell
git commit -m "feat: complete identity-safe serve daemon"
```

- [ ] **Step 5: Prepare the lead handoff**

Report branch, commit SHAs, changed files, serial/single-session enforcement, durable record path and schema, exact test/build outcomes, service refactor resistance, and Rust-forced flattened payload/handshake decisions. Do not push, merge, or open a pull request.
