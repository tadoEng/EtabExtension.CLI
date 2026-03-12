# EtabExtension.CLI — Command Reference

> **Read ARCHITECTURE.md first.** This document covers every command's flags, JSON output shape, error conditions, and example invocations in full detail.

---

## Global contract

Every command:
- Writes **exactly one JSON object** to **stdout** and exits
- Writes all progress/debug lines to **stderr** (Rust ignores these)
- Returns exit code **0** on success, **1** on failure
- JSON always has the envelope `{ "success": bool, "data"?: {...}, "error"?: string, "timestamp": string }`

```
etab-cli <command> [flags]
```

---

## Quick reference

| Command | Mode | Needs ETABS running? | Writes files? |
|---|---|---|---|
| `get-status` | A (attach) | Yes | No |
| `open-model` | A (attach) | Yes | No |
| `close-model` | A (attach) | Yes | No |
| `unlock-model` | A (attach) | Yes | No |
| `generate-e2k` | B (hidden) | No | Yes (`.e2k`) |
| `extract-materials` | B (hidden) | No | Yes (`.parquet`) |
| `run-analysis` | B (hidden) | No | Yes (ETABS sidecars) |
| `extract-results` | B (hidden) | No | Yes (`.parquet` × N) |

---

## `get-status`

Attaches to the user's currently running ETABS instance and reports its state. Does not open or change any model.

```
etab-cli get-status
```

**Flags:** none.

**Success output:**
```json
{
  "success": true,
  "data": {
    "isRunning": true,
    "pid": 12345,
    "etabsVersion": "22.7.0",
    "openFilePath": "C:\\Models\\building.EDB",
    "isLocked": true,
    "isAnalyzed": true,
    "unitSystem": {
      "force": "kip",
      "length": "ft",
      "temperature": "F",
      "isUS": true,
      "isMetric": false
    }
  },
  "timestamp": "2026-03-06T04:00:00Z"
}
```

**Failure conditions:**
- `ETABS is not running` — no ETABS process found via COM
- `Failed to connect to ETABS` — COM attach failed

**Notes:**
- `isLocked = true` means analysis has been run and the model is in post-analysis state. `unlock-model` clears this.
- `isAnalyzed` reflects whether valid analysis results currently exist in the open model.
- `openFilePath` will be empty string if no file is open.

---

## `open-model`

Opens an `.edb` file in ETABS. Two variants:

**Variant A (default) — open in the running ETABS instance:**
```
etab-cli open-model --file <path.edb> [--save] [--new-instance]
```

| Flag | Alias | Required | Description |
|------|-------|----------|-------------|
| `--file` | `-f` | Yes | Path to `.edb` file to open |
| `--save` | | No | Save the currently open model before switching. Default: false |
| `--new-instance` | | No | Spawn a new **visible** ETABS window instead of reusing the running one |

**Success output:**
```json
{
  "success": true,
  "data": {
    "filePath": "C:\\Models\\building.EDB",
    "savedPrevious": false,
    "openedInNewInstance": false
  },
  "timestamp": "..."
}
```

**Failure conditions:**
- `File not found: <path>` — `.edb` does not exist
- `ETABS is not running` — no instance to attach to (when `--new-instance` is not set)
- `Failed to open file` — ETABS returned a non-zero error code from `OpenFile()`

**Notes:**
- When `--new-instance` is used, the new ETABS window is **visible** (not hidden). This is intentional — it is opened for the user to interact with, not for automated Mode B extraction. For automated hidden operations, use the Mode B commands directly.
- Rust typically calls `open-model` before a Mode A workflow (e.g. engineer reviews the model), not before Mode B commands (which open their own hidden instance).

---

## `close-model`

Clears the ETABS workspace without closing ETABS itself. Equivalent to `File → New Model` but without opening the new-model wizard. Uses `InitializeNewModel()` internally — **never triggers a save dialog**.

```
etab-cli close-model [--save]
```

| Flag | Alias | Required | Description |
|------|-------|----------|-------------|
| `--save` | | No | Save the currently open model before clearing. Default: false |

**Success output:**
```json
{
  "success": true,
  "data": {
    "saved": false
  },
  "timestamp": "..."
}
```

**Failure conditions:**
- `ETABS is not running` — no instance to attach to
- `Failed to save model` — only when `--save` is set and the save fails

**Notes:**
- This does not exit ETABS. After `close-model`, ETABS is still running with an empty workspace.
- Useful between batch processing steps to release the `.edb` file lock.

---

## `unlock-model`

Clears the post-analysis lock on the currently open model. After `run-analysis`, ETABS locks the model to protect the analysis results. This command re-enables editing.

```
etab-cli unlock-model --file <path.edb>
```

| Flag | Alias | Required | Description |
|------|-------|----------|-------------|
| `--file` | `-f` | Yes | Path to `.edb` — used to verify the correct file is open |

**Success output:**
```json
{
  "success": true,
  "data": {
    "filePath": "C:\\Models\\building.EDB",
    "wasLocked": true,
    "unlocked": true
  },
  "timestamp": "..."
}
```

**Failure conditions:**
- `ETABS is not running`
- `File mismatch: expected <path>, ETABS has <other>` — the wrong file is open
- `Model was not locked` — succeeds with `wasLocked: false`, not an error
- `Failed to unlock model` — ETABS API returned error

**Notes:**
- Unlocking does **not** delete analysis results. It simply re-enables the editing UI.
- Analysis results remain accessible via `extract-results` regardless of lock state.

---

## `generate-e2k`

Exports an `.edb` model to `.e2k` text format using a hidden ETABS instance (Mode B). The `.e2k` format is ETABS's plain-text model definition — useful for version control and diff-based change detection.

```
etab-cli generate-e2k --file <path.edb> --output <path.e2k> [--overwrite]
```

| Flag | Alias | Required | Description |
|------|-------|----------|-------------|
| `--file` | `-f` | Yes | Path to source `.edb` file |
| `--output` | `-o` | Yes | Path to write `.e2k` output |
| `--overwrite` | | No | Overwrite existing output file. Default: false (fails if exists) |

**Success output:**
```json
{
  "success": true,
  "data": {
    "inputFile": "C:\\Models\\building.EDB",
    "outputFile": "C:\\Models\\building.e2k",
    "fileSizeBytes": 2457600
  },
  "timestamp": "..."
}
```

**Failure conditions:**
- `File not found: <path>` — source `.edb` does not exist
- `Output file already exists: <path>` — when `--overwrite` is not set
- `ETABS failed to export e2k` — ETABS API returned error
- `Fatal error: ...` — unhandled exception (ETABS failed to start, COM error, etc.)

**Notes:**
- ETABS is started hidden and exited automatically. The `.edb` file is not modified.
- The output `.e2k` file size depends on model complexity. Large models can produce multi-MB text files.

---

## `run-analysis`

Runs structural analysis on an `.edb` file using a hidden ETABS instance (Mode B). Analysis results are written to ETABS **sidecar files** alongside the `.edb` — the `.edb` itself is **not saved** after analysis.

```
etab-cli run-analysis --file <path.edb> [--cases CASE1 CASE2 ...] [--units US_Kip_Ft]
```

| Flag | Alias | Required | Description |
|------|-------|----------|-------------|
| `--file` | `-f` | Yes | Path to `.edb` file |
| `--cases` | `-c` | No | Specific load case names to run. Space-separated or repeat flag. Omit = all cases. |
| `--units` | `-u` | No | Unit preset. Default: `US_Kip_Ft`. See unit preset table in ARCHITECTURE.md. |

**Success output:**
```json
{
  "success": true,
  "data": {
    "caseCount": 12,
    "finishedCaseCount": 12,
    "analysisTimeMs": 94300,
    "units": {
      "force": "kip",
      "length": "ft",
      "temperature": "F",
      "isUS": true,
      "isMetric": false
    }
  },
  "timestamp": "..."
}
```

**Failure conditions:**
- `Unknown unit preset '<x>'. Valid values: ...` — invalid `--units` value; fails before ETABS starts
- `File not found: <path>`
- `Analysis did not complete: <n> of <total> cases finished` — partial analysis failure
- `Fatal error: ...` — ETABS crash, COM error, or unhandled exception

**Notes — why `SaveFile()` is never called:**
ETABS writes analysis results to sidecar files alongside the `.edb`: `.Y*`, `.K_*`, `.msh`. These are written directly to disk during analysis. Calling `SaveFile()` after analysis overwrites the `.edb` from in-memory model state, which **does not include the sidecar results** — it would silently delete all results. The correct exit is `ApplicationExit(false)`, which lets ETABS clean up normally and preserves the sidecar files intact. Do not add a `SaveFile()` call to this command.

**Case selection:**
- `--cases DEAD LIVE` — only those named cases run
- `--cases` omitted — all cases in the model run

---

## `extract-materials`

Extracts a single ETABS geometry/material database table to a `.parquet` file using a hidden ETABS instance (Mode B). No load case selection is applied — material and geometry tables are load-independent.

```
etab-cli extract-materials \
  --file <path.edb> \
  --output-dir <dir> \
  [--table-key "Material List by Story"] \
  [--units US_Kip_Ft] \
  [--field-keys ColA ColB ...]
```

| Flag | Alias | Required | Description |
|------|-------|----------|-------------|
| `--file` | `-f` | Yes | Path to `.edb` file |
| `--output-dir` | `-o` | Yes | Directory to write the output file into |
| `--table-key` | `-t` | No | ETABS database table key. Default: `"Material List by Story"` |
| `--units` | `-u` | No | Unit preset. Default: `US_Kip_Ft` |
| `--field-keys` | | No | Specific column names to include (space-separated). Default: all columns |

**Output filename:** derived from the table key slug: `{table_key_slug}.parquet`

Examples:
- `"Material List by Story"` → `material_list_by_story.parquet`
- `"Section Properties"` → `section_properties.parquet`

**Success output (rows found):**
```json
{
  "success": true,
  "data": {
    "filePath": "C:\\Models\\building.EDB",
    "outputDir": "C:\\sidecar",
    "tableKey": "Material List by Story",
    "outputFile": "C:\\sidecar\\material_list_by_story.parquet",
    "rowCount": 142,
    "discardedRowCount": 3,
    "units": {
      "force": "kip",
      "length": "ft",
      "temperature": "F",
      "isUS": true,
      "isMetric": false
    },
    "extractionTimeMs": 4200
  },
  "timestamp": "..."
}
```

**Success output (0 rows — not an error):**
```json
{
  "success": true,
  "data": {
    "filePath": "...",
    "outputDir": "...",
    "tableKey": "Material List by Story",
    "outputFile": null,
    "rowCount": 0,
    "discardedRowCount": 0,
    "units": { ... },
    "extractionTimeMs": 1800
  },
  "timestamp": "..."
}
```

When `outputFile` is null, no `.parquet` file was written (the table returned 0 rows). This is `success: true` — an empty table is valid data.

**Failure conditions:**
- `Unknown unit preset '<x>'. Valid values: ...` — invalid `--units`; fails before ETABS starts
- `File not found: <path>`
- `Table not found: "<key>"` — ETABS does not know a table by that key
- `Fatal error: ...` — unhandled exception

**Notes:**
- `discardedRowCount` counts rows where every field was an empty string. ETABS sometimes returns spurious blank rows.
- The output directory is created if it does not exist.
- `--field-keys` is useful when only a few columns are needed by the downstream Rust code, to keep the Parquet file small.

---

## `extract-results`

Extracts multiple ETABS results tables in a single hidden ETABS session (Mode B). All requested tables are attempted; individual table failures do not abort the session.

```
etab-cli extract-results \
  --file <path.edb> \
  --output-dir <dir> \
  --request '<json>'
```

| Flag | Alias | Required | Description |
|------|-------|----------|-------------|
| `--file` | `-f` | Yes | Path to `.edb` file |
| `--output-dir` | `-o` | Yes | Directory to write all `.parquet` files into |
| `--request` | `-r` | Yes | JSON object with `"units"` (optional) and `"tables"` (required) |

### Request JSON schema

```json
{
  "units": "US_Kip_Ft",
  "tables": {
    "<tableKey>": {
      "loadCases":  null | ["*"] | ["DEAD", "LIVE", ...],
      "loadCombos": null | ["*"] | ["ENV-MAX", ...],
      "groups":     null | ["PierGroup", ...],
      "fieldKeys":  null | ["ColA", "ColB", ...]
    }
  }
}
```

**`units`** — optional. Defaults to `US_Kip_Ft` when omitted. Must be a valid preset string (see ARCHITECTURE.md). Validated before ETABS starts.

**`tables`** — required. Keys are the `TableSelections` JSON property names (camelCase). Each value is a `TableFilter` object.

**Load filter semantics** (applies to `loadCases` and `loadCombos` independently):

| Value | Meaning |
|-------|---------|
| `null` or omitted | **Select nothing** from that category. For geometry tables this is correct — they have no load dependency. For results tables, passing `null` for both cases and combos yields rows from whatever ETABS has in its current display selection (unpredictable — avoid). |
| `["*"]` | **Select all** items of that category from the model |
| `["X", "Y"]` | Select **exactly those named items** |

**`groups`** — optional. When set, queries are scoped to those ETABS groups (e.g. named story groups or pier groups). `null` = whole model.

**`fieldKeys`** — optional. Subset of column names to extract. `null` = all columns.

### Available tables

| `tables` key | Output file | `RequiresAnalysis` | Supports load cases | Supports load combos | Supports groups |
|---|---|---|---|---|---|
| `storyDefinitions` | `story_definitions.parquet` | No | — | — | No |
| `pierSectionProperties` | `pier_section_properties.parquet` | No | — | — | Yes |
| `baseReactions` | `base_reactions.parquet` | **Yes** | Yes | Yes | No |
| `storyForces` | `story_forces.parquet` | **Yes** | Yes | Yes | No |
| `jointDrifts` | `joint_drifts.parquet` | **Yes** | Yes | No | Yes |
| `pierForces` | `pier_forces.parquet` | **Yes** | No | Yes | Yes |
| `modalParticipatingMassRatios` | `modal_participating_mass_ratios.parquet` | **Yes** | No | No | No |

`RequiresAnalysis = true` means the table will be **skipped** (not failed) when the model has not been analyzed. The service checks `IsLocked()` and `AreAllCasesFinished()` upfront and skips those tables with a clear skip message on stderr. Geometry tables (`RequiresAnalysis = false`) always run.

### Example requests

**All tables, all load cases, US units:**
```json
{
  "units": "US_Kip_Ft",
  "tables": {
    "storyDefinitions":            {},
    "pierSectionProperties":       {},
    "baseReactions":               { "loadCases": ["*"], "loadCombos": ["*"] },
    "storyForces":                 { "loadCases": ["*"], "loadCombos": ["*"] },
    "jointDrifts":                 { "loadCases": ["*"] },
    "pierForces":                  { "loadCombos": ["*"] },
    "modalParticipatingMassRatios": {}
  }
}
```

**Selected cases only, SI units, pier group scoped:**
```json
{
  "units": "SI_kN_m",
  "tables": {
    "storyDefinitions": {},
    "baseReactions": {
      "loadCases": ["DEAD", "LIVE", "EQX", "EQY"],
      "loadCombos": ["ENV-LRFD-MAX", "ENV-LRFD-MIN"]
    },
    "jointDrifts": {
      "loadCases": ["EQX", "EQY"],
      "groups": ["DriftJoints"]
    },
    "pierForces": {
      "loadCombos": ["ENV-LRFD-MAX", "ENV-LRFD-MIN"],
      "groups": ["CorePiers", "PerimPiers"]
    }
  }
}
```

**Geometry only (no analysis required):**
```json
{
  "tables": {
    "storyDefinitions":      {},
    "pierSectionProperties": { "groups": ["MainPiers"] }
  }
}
```

### Success output

```json
{
  "success": true,
  "data": {
    "filePath": "C:\\Models\\building.EDB",
    "outputDir": "C:\\sidecar",
    "tables": {
      "story_definitions": {
        "success": true,
        "outputFile": "C:\\sidecar\\story_definitions.parquet",
        "rowCount": 14,
        "discardedRowCount": 0,
        "error": null,
        "extractionTimeMs": 310
      },
      "base_reactions": {
        "success": true,
        "outputFile": "C:\\sidecar\\base_reactions.parquet",
        "rowCount": 840,
        "discardedRowCount": 2,
        "error": null,
        "extractionTimeMs": 1250
      },
      "pier_forces": {
        "success": false,
        "outputFile": null,
        "rowCount": 0,
        "discardedRowCount": 0,
        "error": "Table returned 0 rows after filtering",
        "extractionTimeMs": 980
      }
    },
    "totalRowCount": 854,
    "succeededCount": 2,
    "failedCount": 1,
    "units": {
      "force": "kip",
      "length": "ft",
      "temperature": "F",
      "isUS": true,
      "isMetric": false
    },
    "extractionTimeMs": 3200
  },
  "timestamp": "..."
}
```

The top-level `"success": true` means the session completed without a fatal error. Individual table `"success": false` entries are per-table failures — the Rust caller should check `data.tables[slug].success` for each requested table.

**Top-level failure conditions** (result in `"success": false` at the envelope level):
- `Unknown unit preset '<x>'. Valid values: ...` — invalid `"units"` in JSON
- `Invalid --request JSON: <parse error>` — malformed JSON
- `--request JSON deserialised to null`
- `File not found: <path>`
- `Fatal error: ...` — ETABS crash or unhandled exception

**Per-table failure conditions** (result in `tables[slug].success = false`):
- `Table skipped: model is not analyzed` — `RequiresAnalysis = true` table on an un-analyzed model
- `ETABS returned error code <n> querying table "<key>"` — COM error from DatabaseTables
- `Table not found: "<key>"` — ETABS doesn't recognise the table key

---

## Stderr progress format

All commands write progress lines to stderr using Unicode symbols. Rust ignores these; they are for human monitoring and the VisualTest TUI.

| Prefix | Meaning |
|--------|---------|
| `ℹ ` | Informational step (e.g. `ℹ Opening: building.EDB`) |
| `✓ ` | Step succeeded (e.g. `✓ ETABS started hidden (v22.7.0)`) |
| `✗ ` | Step failed (e.g. `✗ Table 'Pier Forces' returned error code 1`) |
| `⚠ ` | Warning (e.g. `⚠ Table skipped: model is not analyzed`) |

Progress line examples from a typical `extract-results` run:
```
ℹ extract-results: 7 tables requested → C:\sidecar
ℹ Starting ETABS (hidden)...
✓ ETABS started hidden (v22.7.0)
ℹ Opening: 1350_FS_OPT 4E_v1.0_MCE.EDB
✓ Model opened
ℹ Units normalised: kip/ft/F → kip/ft/F (no change needed)
ℹ Extracting: Story Definitions
✓ story_definitions: 14 rows → story_definitions.parquet  (310ms)
ℹ Extracting: Base Reactions  [cases: *, combos: *]
✓ base_reactions: 840 rows → base_reactions.parquet  (1250ms)
⚠ pier_forces: skipped (model not analyzed)
✓ Extraction complete: 2/3 tables  854 rows  (3200ms)
```
