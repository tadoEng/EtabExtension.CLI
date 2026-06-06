# Agent Guide

This repository is the C# ETABS sidecar used by the Rust EtabExtension workspace.
It is intentionally a small command process: each invocation returns one JSON
envelope on stdout and writes progress to stderr.

## Core Contract

- Keep stdout clean. Only `Result.ExitWithResult()` may write the JSON envelope.
- Send all progress, warnings, and diagnostics to `Console.Error`.
- Top-level failures return `Result.Fail<T>` and exit non-zero.
- Per-table extraction failures are partial failures. Continue the table loop and
  report each failed table in the returned `tables` map.
- Every hidden ETABS command that creates an `ETABSApplication` owns cleanup in a
  `finally` block with `ApplicationExit(false)` and `Dispose()`.
- Session helpers in `Shared/Infrastructure/Etabs/EtabsSessionHelpers.cs` must not
  open, exit, or dispose ETABS. They operate only on an already-open session.

## Command Modes

Mode A commands attach to a user-visible ETABS instance:

- `get-status`
- `open-model`
- `close-model`
- `unlock-model`

Mode B commands create their own hidden ETABS session:

- `generate-e2k`
- `generate-e2k-corpus`
- `extract-materials`
- `run-analysis`
- `extract-results`
- `analyze-and-extract`
- `snapshot-export`

Prefer combined-session commands when Rust would otherwise call two Mode B
commands on the same `.edb`. `analyze-and-extract` opens ETABS once, runs
analysis, extracts tables, collects metadata, then exits once. `snapshot-export`
opens ETABS once, exports E2K, extracts snapshot/material tables, collects
metadata, then exits once.

`generate-e2k-corpus` is the parser-oracle generator. It starts one hidden
ETABS session, creates each requested model through EtabSharp/CSI API calls,
saves `model.edb`, exports `model.e2k`, and writes a hash-pinned
`model.meta.toml`. It must never create or modify a model by rewriting or
re-importing E2K text.

Use `generate-e2k-corpus --output-dir <dir> --plan-version 22|23` to generate
the authoritative pairwise subset assigned to one installed ETABS major
version. Use `--request <json>` for explicit custom cases; provide exactly one
planning mode. When multiple ETABS versions are installed, pass
`--etabs-path <path-to-ETABS.exe>`; the adapter validates the path and forwards
it to Cardex's `ETABSWrapper.CreateNew(programPath)`.

Before adding or changing a CSI ETABS API call, verify the contract in
`D:\Work\Cardex`. Check every CSI return code through EtabSharp or explicitly at
the call site.

## Current Performance Notes

- The sidecar now supports the P1 desktop path fix through
  `analyze-and-extract`.
- The commit path is supported through `snapshot-export` so Rust can avoid
  `generate-e2k` followed by `extract-materials` on the same model.
- `extract-results`, `analyze-and-extract`, and `snapshot-export` share the same
  open-model table extraction helper.
- `EtabsTableQueryService` caches load case and load combo names per extraction
  run and exposes `ResetSelectionAsync()` so callers reset display selection once
  at the end.
- `model-metadata.json` uses schema version 2 and includes load patterns, cases,
  combos, stories, groups, materials, frame/area sections, and category warnings.
- Combined-session commands also write `run-metrics.json` with phase timings for
  ETABS startup, model open, analysis/export, table extraction, metadata, and
  artifact writes.
- Flat command mode supports extraction profiles: `full`, `results`,
  `geometry`, and `snapshot`. JSON requests can set `extractionProfile`; if the
  `tables` object is empty, the profile supplies the table selection.

## Where To Look

- Command registration: `src/EtabExtension.CLI/Program.cs`
- Feature commands/services: `src/EtabExtension.CLI/Features/*`
- Combined-session helpers:
  `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/EtabsSessionHelpers.cs`
- Table selection/query behavior:
  `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Table`
- Unit presets:
  `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Unit`
- Metadata models:
  `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Metadata`
- Run metrics models:
  `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Metrics`
- Parquet writing:
  `src/EtabExtension.CLI/Shared/Infrastructure/Parquet`

## Safe Change Checklist

Before closing a sidecar change, run:

```powershell
dotnet test EtabExtension.CLI.Tests\EtabExtension.CLI.Tests.csproj
dotnet build EtabExtension.CLI.slnx
```

For Rust integration changes in the sibling workspace, also run the relevant
`cargo check`/`cargo test` commands from `D:\Work\EtabExtension`.
