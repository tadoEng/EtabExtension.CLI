# Serve Daemon Core Design

## Goal

Complete issue `tadoEng/EtabExtension#188` by making `etab-cli serve` the single owner of one long-lived ETABS process, dispatching every supported command serially over line-delimited JSON, and safely tracking the identity of the managed ETABS process across daemon crashes.

The change is limited to `D:\Work\EtabExtension.CLI`. The Rust client and Cardex repositories are read-only references.

## Protocol

The daemon reads one compact JSON request per stdin line:

```json
{"id":1,"command":"analyze-and-extract","request":{"filePath":"C:\\model.edb","outputDir":"C:\\results"}}
```

It writes exactly one compact response line for each request:

```json
{"id":1,"success":true,"error":null,"data":{}}
```

Human-readable progress, warnings, orphan diagnostics, and shutdown messages go only to stderr. The daemon emits one id-less startup handshake line before reading requests:

```json
{"protocol":"etab-cli-serve","version":1}
```

The merged Rust client explicitly ignores id-less JSON objects while waiting for a correlated response. It converts the existing CLI arguments into a flattened `request` object: locator fields such as `filePath` and `outputDir` coexist with the existing per-command request fields. The C# dispatcher must consume that exact flattened shape.

Malformed JSON and command failures produce correlated failure envelopes without terminating the loop. A `shutdown` request receives a success envelope before the loop exits. EOF exits without a response. Both paths dispose the shared ETABS session exactly once.

## Serial Dispatch

`ServeLoop` awaits each dispatch and response write before reading the next input line. It does not queue background ETABS work. This provides arrival-order execution and prevents overlapping COM calls.

The daemon uses one dependency-injection scope for its full lifetime. The scope contains one session owner and the feature services used by the dispatcher. The dispatcher supports:

- `get-status`
- `open-model`
- `snapshot-export`
- `analyze-and-extract`
- `unlock-model`
- `close-model`
- `extract-results`
- `extract-materials`
- `generate-e2k`
- `read-model-metadata`

The asynchronous operation commands from issue #233 are not registered or modeled.

## Shared ETABS Ownership

The daemon session owner is the only component that may launch, exit, or dispose the managed ETABS application. It launches lazily on the first command that needs ETABS, caches the application, and reuses it for all later commands.

Existing one-shot commands remain backward-compatible. Feature services are split into:

1. a one-shot lifecycle wrapper that retains current CLI behavior; and
2. an operation method that accepts an already-open, caller-owned ETABS application and never launches, attaches, exits, or disposes it.

The serve dispatcher calls only the caller-owned operation methods. Shared helpers under `EtabsSessionHelpers` continue to operate only on an already-open session.

`get-status` does not launch ETABS merely to poll. Before launch it reports `isRunning: false`; after launch it reports only the daemon-owned instance. `open-model` always opens in the shared instance, so its `newInstance` request flag cannot create a second process.

Visibility changes are idempotent. The launcher checks `Visible` before calling `Hide`, because ETABS rejects a second hide call.

## Process Identity

Process interactions are isolated behind interfaces so unit tests never start ETABS:

- an owned-application launcher returns the ETABS application plus its process identity;
- a process inspector reads a process by PID and returns its current identity;
- a session-record store persists and clears the durable record;
- an orphan terminator kills only a fully verified managed process.

The process identity tuple is:

- PID;
- process start time in UTC;
- normalized executable path;
- managed launch record UUID.

The record is written atomically to:

`%LOCALAPPDATA%\EtabExtension\sidecar\managed-etabs-session.json`

Its versioned JSON shape is:

```json
{
  "schemaVersion": 1,
  "pid": 1234,
  "processStartTimeUtc": "2026-07-15T08:30:00.0000000Z",
  "executablePath": "C:\\Program Files\\Computers and Structures\\ETABS 23\\ETABS.exe",
  "managedLaunchRecordId": "b956bd77-7254-44f6-bab4-d7381ae1187d",
  "createdAtUtc": "2026-07-15T08:30:01.0000000Z"
}
```

The file is written through a temporary sibling and atomically replaced/moved so a daemon crash cannot leave a partially written record.

Before every use of the cached application, the owner inspects the PID and compares PID, UTC start time, normalized executable path, and the in-memory managed launch record UUID with the durable record. Bare-PID equality is never accepted. Any mismatch invalidates the session and returns a clean-reopen error rather than attaching to or operating on an unverified process.

On normal shutdown, the owner exits the application without saving, disposes the wrapper, and clears the durable record. Cleanup is idempotent.

## Startup Orphan Handling

Before the handshake is emitted, daemon startup checks the durable record:

- Missing record: continue normally.
- Invalid/unreadable record: report diagnostics to stderr, clear it, and continue without targeting a process.
- PID absent: report the stale record, clear it, and continue.
- PID present but start time or executable path differs: treat it as PID reuse or an unrelated process, never terminate it, clear the stale record, and continue.
- Full OS tuple match: treat it as a surviving managed orphan. Report the identity and clean-reopen requirement, terminate it without saving or adoption, wait for exit, clear the record, and continue.

The managed launch record UUID identifies the launch record and must be present and valid, but it is not inferred from a live OS process. A live process is eligible for termination only when the stored record is structurally valid and its PID, start time, and executable path all match the inspected process.

## Error Handling

Transport errors remain per-request failures whenever framing permits correlation. Unsupported commands, missing request payloads, deserialization failures, identity-verification failures, and feature failures return `success: false` with the original request ID. The loop continues after these failures.

Per-table extraction failures retain their existing partial-failure representation inside `data.tables`; the transport must not convert them into top-level failures.

If stdout itself cannot be written, the daemon cannot preserve the protocol and may terminate. ETABS cleanup still runs in the command's `finally` path.

## Testing

No automated test launches or attaches to ETABS. Tests use fake dispatchers, fake applications/session handles, fake process inspectors, in-memory or temporary-file record stores, and fake terminators.

Coverage includes:

- startup handshake framing and stdout purity;
- one response line per request and request-ID correlation;
- malformed request and dispatcher exception envelopes;
- strictly serial, non-overlapping dispatch in arrival order;
- shutdown and EOF cleanup;
- one lazy launch reused across multiple commands;
- all required command routes and flattened Rust request shapes;
- durable record serialization and round-trip;
- full identity tuple verification before reuse;
- rejection of PID-only, start-time, path, or record-ID mismatches;
- matching orphan termination without adoption or save;
- stale record/PID reuse protection;
- idempotent hide and session cleanup;
- compatibility of existing one-shot commands.

The completion gates are:

```powershell
dotnet test EtabExtension.CLI.Tests\EtabExtension.CLI.Tests.csproj
dotnet build EtabExtension.CLI.slnx
```

Live ETABS validation remains manual and lead-supervised.

## Non-Goals

- No Rust, TypeScript, Tauri, or Cardex changes.
- No async operation envelope, polling, cancellation, or event streaming from issue #233.
- No orphan adoption or recovery of unsaved model state.
- No real ETABS process launch in automated tests.
