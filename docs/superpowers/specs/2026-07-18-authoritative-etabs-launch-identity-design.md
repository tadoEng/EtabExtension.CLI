# Authoritative ETABS Launch Identity Design

Date: 2026-07-18
Status: Lead approved
Issue: tadoEng/EtabExtension#238

## Goal

Make the managed ETABS session identity authoritative from process creation so a concurrent user-started ETABS instance can never be recorded or later terminated as the daemon-owned process.

## Scope

This change is confined to the ETABS launcher/session infrastructure and its fake-seam tests on `codex/launcher-pid-com-identity`. It does not change `EtabsSession`, `OrphanSessionCleaner`, serve inspection commands, the durable record schema, or any model workflow.

## Architecture

`ManagedEtabsLauncher` resolves an ETABS executable, starts it through `System.Diagnostics.Process`, and immediately captures PID, UTC start time, and `MainModule.FileName` from that owned process object. That tuple is the only identity supplied to `ManagedEtabsApplication` and the durable session record.

The launcher polls `ETABSWrapper.ConnectToProcess(ownedPid)` for at most 60 seconds with 100-millisecond intervals. Each unsuccessful connection attempt disposes any partial wrapper. Once attached, it calls `Hide()` only when `Visible()` is true and transfers ownership of both the COM wrapper and the process handle to `ManagedEtabsApplication`.

The old before/after ETABS process enumeration remains only as a diagnostic cross-check. It never selects or replaces the authoritative PID. When the new-process candidate set is not exactly the owned PID, stderr receives:

```text
⚠ Managed ETABS launch cross-check disagreed with authoritative owned PID <pid>: snapshot candidates=[<sorted comma-separated pids>]. Authoritative owned-process identity retained.
```

## Executable Resolution

The configuration key is `EtabsExePath`, supplied through the host's normal `IConfiguration` sources, including environment variables and command-line configuration.

Resolution order:

1. `EtabsExePath` when configured. A configured path must name an existing file; an invalid explicit path fails immediately.
2. Windows uninstall registry entries under HKLM/HKCU, including the 32-bit HKLM view, whose display name starts with `ETABS`. Candidates use `DisplayIcon` when it identifies an executable, otherwise `InstallLocation\ETABS.exe`. Higher display versions sort first.
3. Default install directories under `%ProgramFiles%\Computers and Structures\ETABS *\ETABS.exe`, sorted by descending parsed version/name.

Failure codes are stable and included in exception messages returned through the existing serve error envelope:

- `ETABS_EXECUTABLE_NOT_FOUND`: explicit `EtabsExePath` does not exist.
- `ETABS_EXECUTABLE_UNRESOLVED`: no configured, registry, or default candidate exists.
- `ETABS_PROCESS_START_FAILED`: the resolved executable could not be started.
- `ETABS_PROCESS_IDENTITY_FAILED`: PID/start time/main-module path could not be captured from the owned process.
- `ETABS_ATTACH_TIMEOUT`: the owned process did not accept `ConnectToProcess` before the deadline.

## Failure and Cleanup

Before ownership transfer, every failure kills only the process returned by this launcher's `Process.Start`, waits briefly for exit, and disposes its process handle. Partial COM wrappers are disposed between retries. After ownership transfer, existing session shutdown continues to call `ApplicationExit(false)` and dispose the wrapper; the managed handle also disposes the process handle.

No snapshot candidate is ever terminated or recorded. A foreign ETABS launch can only alter the diagnostic candidate list.

## Live Certification Boundary

Automated tests must not launch ETABS. The production polling code carries an explicit TODO for supervised live certification of two unknowns: whether a plainly started `ETABS.exe` accepts `ConnectToProcess` before a model opens, and observed readiness latency. The initial 60-second deadline and 100-millisecond interval favor safe startup tolerance while keeping cancellation-free synchronous launcher behavior bounded.

## Tests

Fake seams prove:

- the durable identity comes from the owned process object;
- a concurrent foreign snapshot candidate is never recorded;
- attach retries use only the owned PID;
- deadline failure kills only the owned PID and disposes failed attempts;
- missing discovery and invalid explicit configuration produce stable error codes;
- snapshot disagreement writes the exact warning while retaining the owned identity;
- existing session/orphan behavior remains green;
- no automated test starts real ETABS.

Completion gates are the full xUnit project and full solution build with zero errors.
