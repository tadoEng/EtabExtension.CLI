# Serve Async Operation Envelope Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a generic asynchronous daemon operation envelope and migrate `analyze-and-extract` onto it without changing the frozen Rust client's synchronous contract.

**Architecture:** Keep `ServeLoop` as a responsive line-protocol control loop and introduce a daemon-scoped operation manager backed by one dedicated STA worker thread. The manager owns the single-operation lease, immutable status snapshots, cooperative cancellation, and a sequenced event journal with a bounded memory tail plus JSONL spill files under `%LOCALAPPDATA%\EtabExtension\sidecar\operations\<operationId>\events.jsonl`. Existing synchronous COM commands are marshalled to the same STA worker; legacy `analyze-and-extract` starts an operation and waits for its original `Result<AnalyzeAndExtractData>`.

**Tech Stack:** C# 14 / .NET 10, `System.Text.Json`, `BlockingCollection`, xUnit v3.

---

### Task 1: Protocol contracts and event journal

**Files:**
- Create: `src/EtabExtension.CLI/Features/Serve/Operations/OperationContracts.cs`
- Create: `src/EtabExtension.CLI/Features/Serve/Operations/OperationEventJournal.cs`
- Test: `EtabExtension.CLI.Tests/OperationEventJournalTests.cs`

- [ ] Define camel-case request/result records for `start-operation`, `get-operation-status`, `get-operation-events`, and `cancel-operation`; use `operationId` and `sinceSeq` on the wire while preserving the existing outer `{id, success, data}` envelope.
- [ ] Implement an append-only journal that assigns sequence numbers under one lock, retains only the configured tail in memory, writes every event as one compact JSON line to the per-operation spill file, and replays `seq > sinceSeq` by combining spill history with the memory tail without duplicates.
- [ ] Prove with xUnit that sequences are strictly monotonic, `sinceSeq` is exclusive, bounded eviction still replays from disk, and spill lines are durable JSON.
- [ ] Run `dotnet test EtabExtension.CLI.Tests\EtabExtension.CLI.Tests.csproj --no-restore --filter OperationEventJournalTests`; expect all journal tests to pass.

### Task 2: STA worker and generic operation manager

**Files:**
- Create: `src/EtabExtension.CLI/Features/Serve/Operations/StaExecutionWorker.cs`
- Create: `src/EtabExtension.CLI/Features/Serve/Operations/OperationManager.cs`
- Test: `EtabExtension.CLI.Tests/OperationManagerTests.cs`

- [ ] Add one long-lived worker thread that calls `SetApartmentState(ApartmentState.STA)` before start, drains queued delegates serially, and completes caller tasks without executing delegates on the protocol thread.
- [ ] Add a generic operation registry keyed by `kind`; an operation definition receives the untouched JSON payload plus an execution context whose `RunStepAsync` updates phase, step index/total, CSI name, heartbeat, step elapsed time, emits events, and checks cancellation only before and after a step.
- [ ] Enforce one active lease: return a structured failed `Result` for a second start while phase is queued/running/cancelling; retain completed operation state for later status/event queries.
- [ ] Track operation-class and step budgets with an injectable clock and expose `suspectedHang=true` after either budget while never terminating the worker or ETABS.
- [ ] Prove immediate start response, live status/events, single-operation rejection, between-step cancellation, suspected hang, and execution on one STA thread with focused fake-operation tests.
- [ ] Run `dotnet test EtabExtension.CLI.Tests\EtabExtension.CLI.Tests.csproj --no-restore --filter OperationManagerTests`; expect all manager tests to pass.

### Task 3: Analyze-and-extract operation and cached daemon status

**Files:**
- Modify: `src/EtabExtension.CLI/Features/AnalyzeAndExtract/IAnalyzeAndExtractService.cs`
- Modify: `src/EtabExtension.CLI/Features/AnalyzeAndExtract/AnalyzeAndExtractService.cs`
- Create: `src/EtabExtension.CLI/Features/Serve/Operations/AnalyzeAndExtractOperation.cs`
- Create: `src/EtabExtension.CLI/Features/Serve/CachedSessionStatus.cs`
- Test: `EtabExtension.CLI.Tests/AnalyzeAndExtractOperationTests.cs`

- [ ] Extend the daemon analyze seam with an optional progress/cancellation context and mark the existing CSI boundaries (`OpenFile`, unit normalization, analysis, status reads, table extraction, metadata collection) as explicit steps; check cancellation between those calls, never by aborting a call in progress.
- [ ] Register `analyze-and-extract` as the first generic operation kind, deserialize the existing flattened payload unchanged, obtain/verify the shared session only on the STA worker, and return the existing `Result<AnalyzeAndExtractData>` as the operation result.
- [ ] Cache the most recently observed daemon `GetStatusData`; while an operation holds the worker, `get-status` returns that snapshot (with managed PID/running fallback) and performs no COM read.
- [ ] Prove the fake analyze operation exposes progress and cancellation boundaries without launching ETABS.

### Task 4: Dispatcher integration and legacy compatibility

**Files:**
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeRequests.cs`
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeDispatcher.cs`
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeExtensions.cs`
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeCommand.cs`
- Modify: `EtabExtension.CLI.Tests/ServeLoopTests.cs`
- Modify: `EtabExtension.CLI.Tests/ServeContractTests.cs`

- [ ] Route the four new commands to the operation manager and keep every response correlated by the original numeric request `id`; do not emit unsolicited response-shaped lines.
- [ ] Marshal all synchronous COM-touching dispatcher routes to the same STA worker; keep control-only status/events/cancel calls on the loop thread.
- [ ] Implement legacy `analyze-and-extract` as start plus internal completion wait and return the exact original result object so the frozen Rust client sees no contract change.
- [ ] Dispose the operation manager/worker during daemon shutdown before disposing the shared session, allowing in-flight cooperative cancellation to settle between calls.
- [ ] Extend transport/contract tests for example requests, immediate `operationId`, polling during fake execution, and the legacy response shape.

### Task 5: Verification and local commits

**Files:**
- Modify only files listed above plus this plan.

- [ ] Run `dotnet test EtabExtension.CLI.Tests\EtabExtension.CLI.Tests.csproj --no-restore`; expect the full xUnit suite to report zero failures and no ETABS launch.
- [ ] Run `dotnet build EtabExtension.CLI.slnx --no-restore`; expect build success with zero errors.
- [ ] Inspect `git diff --check`, `git status --short`, and the commit graph; preserve `.serena/project.yml` and `.codex-worktrees/` as user-owned pre-existing dirt.
- [ ] Commit protocol/journal, manager/threading, and integration/compatibility as separate passing milestones on `codex/serve-async-envelope`; do not push or merge.
