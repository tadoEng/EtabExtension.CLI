# ETABS Extension — Sidecar CLI Development Guide

Architecture, connection strategy, command contracts, and implementation
patterns for `etab-cli.exe` — the C# .NET 10 sidecar that owns all ETABS
COM interaction.

---

## Purpose and Boundaries

The sidecar is the **only component that talks to ETABS COM**.
No Rust code, no Tauri, no agent ever calls ETABS directly.

```
Rust (ext-core)
    │
    └── spawns ──► etab-cli.exe  [stdout=JSON, stderr=progress]
                       │
                       └── COM ──► ETABS.exe (user's instance, Mode A)
                                   OR
                                   ETABS.exe (hidden instance, Mode B)
```

The sidecar is **single-shot**: one command, one job, one JSON result, one exit.
Never a daemon. Never kept alive between calls.

---

## IPC Contract

```
stdin:   nothing
stdout:  ONE JSON object, written once at the end — Result<T>
stderr:  progress lines, written freely during execution
exit:    0 = success  (Result.Success == true)
         1 = failure  (Result.Success == false, Result.Error set)
```

`Program.cs` enforces stdout discipline globally:
```csharp
// ALL Console.WriteLine → stderr
Console.SetOut(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
// Only ExitWithResult() writes to real stdout
```

---

## Connection Modes

### Mode A — Attach to User's Running ETABS

Used by: `get-status`, `open-model`, `close-model`, `unlock-model`

```csharp
// ETABSWrapper.Connect() attaches to the first running ETABS instance.
// Use ConnectToProcess(pid) to attach to a specific instance by PID.
ETABSApplication? app = ETABSWrapper.Connect();
// app.Model.Files, app.Model.Analyze, app.Model.ModelInfo ...
```

**After the command: dispose the app. Never calls `ApplicationExit()`.**
`ETABSApplication.Dispose()` in Mode A only releases the COM proxy — ETABS
keeps running, file stays loaded, user sees nothing change.

```csharp
// ✅ Mode A cleanup — always in finally block
app?.Dispose(); // releases COM RCW — does NOT exit ETABS
```

**Why releasing COM is enough:** `Dispose()` drops the .NET RCW proxy.
ETABS is a native Win32 process — the open file lives entirely inside it.
Releasing the proxy just disconnects the control handle.

### Mode B — New Hidden Instance

Used by: `generate-e2k`, `extract-materials`, `run-analysis`, `extract-results`

```csharp
// ETABSWrapper.CreateNew() starts a fresh ETABS instance.
// Hide immediately so it never appears on screen or taskbar.
ETABSApplication? app = ETABSWrapper.CreateNew();
app.Application.Hide();
// app.Model.Files, app.Model.Analyze ...
```

**After the command: exit then dispose — always in `finally`.**

```csharp
// ✅ Mode B cleanup — always in finally block
app?.Application.ApplicationExit(false); // exit hidden instance
app?.Dispose();                          // release COM RCW
```

### Decision Rule

```
User-facing session commands          Mode A
  get-status, open-model,             attach to running ETABS
  close-model, unlock-model           release COM only on exit

Everything else                       Mode B
  generate-e2k, extract-materials,    fresh hidden instance
  run-analysis, extract-results       ApplicationExit + release on exit
```

No dual-capable commands. No "reuse if running" optimization.
Mode B is always predictable — never risks blocking on a Save dialog
from the user's unsaved working file.

---

## COM Cleanup

`ETABSApplication.Dispose()` handles COM release internally via EtabSharp.
You do not need `ComCleanup` in service code — just call `app?.Dispose()` in `finally`.

```csharp
Mode A:  app?.Dispose()                              // release COM only
Mode B:  app?.Application.ApplicationExit(false)     // exit hidden ETABS
         app?.Dispose()                              // release COM
```

`ComCleanup.cs` is no longer needed in service layer code. It can be removed
or kept as a low-level fallback for edge cases only.

---

## Result Pattern

Every service method returns `Result<T>`. Command handlers call
`ExitWithResult()` — the only place `Environment.Exit()` is called.

```csharp
// Service signature
Task<Result<TData>> DoSomethingAsync(...);

// Command handler
var result = await service.DoSomethingAsync(...);
Environment.Exit(result.ExitWithResult());
```

Never throw across the service boundary:
```csharp
try
{
    // ... work ...
    return Result.Ok(data);
}
catch (COMException ex)
{
    return Result.Fail<TData>($"ETABS COM error: {ex.Message}")
        with { Data = partialData };
}
catch (Exception ex)
{
    return Result.Fail<TData>($"Unexpected error: {ex.Message}")
        with { Data = partialData };
}
finally
{
    // Mode A: app?.Dispose();
    // Mode B: app?.Application.ApplicationExit(false);
    //         app?.Dispose();
}
```

### JSON Output Shape

```json
{
  "success": true,
  "error": null,
  "timestamp": "2024-02-05T14:30:00Z",
  "data": { }
}
```

```json
{
  "success": false,
  "error": "ETABS is not running",
  "timestamp": "2024-02-05T14:30:00Z",
  "data": {
    "isRunning": false,
    "messages": ["✗ ETABS is not running", "Start ETABS manually first"]
  }
}
```

---

## EtabSharp API Reference

EtabSharp is complete. No changes needed before Phase 1.

### `ETABSWrapper` — factory (static)

```csharp
ETABSApplication? Connect()                    // Mode A: attach to first active ETABS
ETABSApplication? ConnectToProcess(int pid)    // Mode A: attach to specific PID
ETABSApplication? CreateNew(                   // Mode B: start new hidden instance
    string? programPath = null,
    bool startApplication = true)
List<ETABSInstanceInfo> GetAllRunningInstances() // discover all running ETABS
bool IsRunning()
bool IsSupportedVersionRunning()
string? GetActiveVersion()
```

> `CreateNew()` supports env var override: set `CSI_ETABS_API_ETABSObject_PATH`
> to point at a specific ETABS install without changing code.

### `ETABSApplication` — entry point

```csharp
IApplication Application   // lifecycle, visibility, ROT
ETABSModel   Model         // all model operations
string       FullVersion   // e.g. "22.7.0"
double       ApiVersion
void         Close(bool savePrompt = false)
void         Dispose()     // Mode A: release COM only | Mode B: ApplicationExit + release
```

### `IApplication` — wraps `cOAPI`

```csharp
int  ApplicationStart()
int  ApplicationExit(bool savePrompt = false)  // false = exit without saving
int  Hide()                                    // hide window + taskbar
int  Unhide()
bool Visible()
double GetOAPIVersionNumber()
int  SetAsActiveObject()
int  UnsetAsActiveObject()
```

### `IFiles` — accessed via `app.Model.Files`

```csharp
int OpenFile(string filePath)
int SaveFile(string filePath)
int ExportFile(string filePath, eFileTypeIO fileType)
int NewBlankModel()
```

> EtabSharp renames: `Save()` → `SaveFile()`, `NewBlank()` → `NewBlankModel()`

### `eFileTypeIO` enum

```csharp
eFileTypeIO.TextFile        = 1   // .e2k  ← what we use
eFileTypeIO.DBTablesExcel   = 2
eFileTypeIO.DBTablesAccess  = 3
eFileTypeIO.DBTablesText    = 4
eFileTypeIO.DBTablesXML     = 5
```

### `IAnalyze` — accessed via `app.Model.Analyze`

```csharp
int  RunCompleteAnalysis()              // SetAllCasesToRun + CreateAnalysisModel + RunAnalysis
int  RunAnalysis()
int  CreateAnalysisModel()
int  SetAllCasesToRun()
int  SetAllCasesToSkip()
int  SetRunCaseFlag(string caseName, bool run, bool all = false)
List<CaseStatus> GetCaseStatus()        // CaseStatus.IsFinished per case
bool AreAllCasesFinished()
int  DeleteResults(string caseName, bool all = false)
int  DeleteAllResults()
```

### `ISapModelInfor` — accessed via `app.Model.ModelInfo`

```csharp
string GetModelFilename(bool includePath = false)
string GetModelFilepath()
bool   IsLocked()
void   SetLocked(bool isLocked)
string GetVersion()
```

---

## Call Patterns

### Mode A template

```csharp
ETABSApplication? app = null;
try
{
    app = ETABSWrapper.Connect();
    if (app is null)
        return Result.Fail<T>("ETABS is not running");

    // read state via EtabSharp wrappers
    var filename = app.Model.ModelInfo.GetModelFilename(includePath: true);
    bool isLocked = app.Model.ModelInfo.IsLocked();

    // suppress Save dialog if needed before OpenFile()
    // (raw SapModel access for methods not yet wrapped)
    app.SapModel.SetModelIsModified(false);
    app.Model.Files.OpenFile(filePath);

    return Result.Ok(data);
}
finally
{
    app?.Dispose(); // releases COM RCW — does NOT exit ETABS
}
```

### Mode B template

```csharp
ETABSApplication? app = null;
var stopwatch = Stopwatch.StartNew();
try
{
    Console.Error.WriteLine("ℹ Starting ETABS (hidden)...");
    app = ETABSWrapper.CreateNew();
    if (app is null)
        return Result.Fail<T>("Failed to start ETABS");

    app.Application.Hide();

    app.Model.Files.OpenFile(filePath);
    // ... do work ...
    app.Model.Files.SaveFile(filePath); // persist results into .edb

    return Result.Ok(data);
}
finally
{
    app?.Application.ApplicationExit(false); // exit hidden instance
    app?.Dispose();                          // release COM RCW
}
```

---

## Command Catalogue

### `get-status`

Returns ETABS running state, PID, open file, lock state.

```bash
etab-cli get-status
```

**Connection:** Mode A. If ETABS not running → `Result.Ok` with `isRunning: false`.
Not an error — Rust uses this to determine state.

```csharp
// OS check first — no COM needed to detect if ETABS is running
if (!ETABSWrapper.IsRunning())
    return Result.Ok(new GetStatusData { IsRunning = false });

var instances = ETABSWrapper.GetAllRunningInstances();
var pid = instances.FirstOrDefault()?.ProcessId;

ETABSApplication? app = null;
try
{
    app = ETABSWrapper.Connect();
    if (app is null)
        return Result.Fail<GetStatusData>("ETABS is running but could not attach");

    var openFilePath = app.Model.ModelInfo.GetModelFilepath();
    bool isLocked    = app.Model.ModelInfo.IsLocked();
    bool isAnalyzed  = app.Model.Analyze.AreAllCasesFinished();

    return Result.Ok(new GetStatusData
    {
        IsRunning    = true,
        Pid          = pid,
        EtabsVersion = app.FullVersion,
        OpenFilePath = string.IsNullOrEmpty(openFilePath) ? null : openFilePath,
        IsModelOpen  = !string.IsNullOrEmpty(openFilePath),
        IsLocked     = isLocked,
        IsAnalyzed   = isAnalyzed
    });
}
finally
{
    app?.Dispose(); // release COM — does NOT exit ETABS
}
```

**Data shape:**
```json
{
  "isRunning": true,
  "pid": 12345,
  "etabsVersion": "22.0.0.1234",
  "openFilePath": "C:\\...\\working\\model.edb",
  "isModelOpen": true,
  "isLocked": false,
  "isAnalyzed": true
}
```

---

### `open-model`

Opens an `.edb` in the user's running ETABS.
`OpenFile()` implicitly closes whatever is currently open —
no intermediate `InitializeNewModel()` needed.

```bash
etab-cli open-model --file <path> [--save | --no-save]
```

| Flag | Required | Description |
|---|---|---|
| `--file` / `-f` | yes | Path to `.edb` |
| `--save` | no | Save current model before switching |
| `--no-save` | no | Discard changes — default |

**Connection:** Mode A. Hard error if ETABS not running.

```csharp
ETABSApplication? app = null;
try
{
    app = ETABSWrapper.Connect();
    if (app is null)
        return Result.Fail<OpenModelData>("ETABS is not running");

    var currentPath    = app.Model.ModelInfo.GetModelFilepath();
    var hasCurrentFile = !string.IsNullOrEmpty(currentPath);

    if (hasCurrentFile)
    {
        // raw SapModel for GetModelIsModified — not yet in ISapModelInfor
        bool isModified = false;
        app.SapModel.GetModelIsModified(ref isModified);
        if (isModified)
        {
            if (save)
                app.Model.Files.SaveFile(currentPath);
            else
                app.SapModel.SetModelIsModified(false); // suppress Save dialog
        }
    }

    // OpenFile() closes current file, opens new one atomically
    int ret = app.Model.Files.OpenFile(filePath);
    if (ret != 0)
        return Result.Fail<OpenModelData>($"OpenFile failed (ret={ret})");

    var pid = ETABSWrapper.GetAllRunningInstances().FirstOrDefault()?.ProcessId;
    return Result.Ok(new OpenModelData
    {
        FilePath         = filePath,
        PreviousFilePath = hasCurrentFile ? currentPath : null,
        Pid              = pid
    });
}
finally
{
    app?.Dispose(); // release COM — does NOT exit ETABS
}
```

**Data shape:**
```json
{
  "filePath": "C:\\...\\working\\model.edb",
  "previousFilePath": null,
  "pid": 12345
}
```

---

### `close-model`

Clears the ETABS workspace leaving ETABS running with a blank model.
Only used by `ext etabs close` — not a prerequisite for `open-model`.

`InitializeNewModel()` confirmed to clear without triggering Save dialogs
even on modified models. This replaces `SetModelIsModified(false)` + `NewBlank()`.

```bash
etab-cli close-model [--save | --no-save]
```

**Connection:** Mode A.

```csharp
ETABSApplication? app = null;
try
{
    app = ETABSWrapper.Connect();
    if (app is null)
        return Result.Fail<CloseModelData>("ETABS is not running");

    var currentPath = app.Model.ModelInfo.GetModelFilepath();

    if (save && !string.IsNullOrEmpty(currentPath))
        app.Model.Files.SaveFile(currentPath);
    else
    {
        bool isModified = false;
        app.SapModel.GetModelIsModified(ref isModified); // raw — not yet wrapped
        // --no-save: no extra step — InitializeNewModel suppresses prompt
    }

    // InitializeNewModel() confirmed: clears workspace without Save dialog
    int ret = app.SapModel.InitializeNewModel(eUnits.kip_ft_F);
    if (ret != 0)
        return Result.Fail<CloseModelData>("InitializeNewModel failed");

    return Result.Ok(new CloseModelData
    {
        ClosedFilePath = currentPath,
        WasSaved       = save
    });
}
finally
{
    app?.Dispose(); // release COM — does NOT exit ETABS
}
```

**Data shape:**
```json
{
  "closedFilePath": "C:\\...\\working\\model.edb",
  "wasSaved": false
}
```

---

### `unlock-model`

Clears the ETABS post-analysis lock.

```bash
etab-cli unlock-model --file <path>
```

**Connection:** Mode A. File must already be open in ETABS.

```csharp
ETABSApplication? app = null;
try
{
    app = ETABSWrapper.Connect();
    if (app is null)
        return Result.Fail<UnlockData>("ETABS is not running");

    var currentPath = app.Model.ModelInfo.GetModelFilepath();
    if (!PathsAreEqual(currentPath, filePath))
        return Result.Fail<UnlockData>(
            "File not open in ETABS. Open it first: ext etabs open");

    bool wasLocked = app.Model.ModelInfo.IsLocked();

    if (wasLocked)
    {
        app.Model.ModelInfo.SetLocked(false);
        // verify it cleared
        if (app.Model.ModelInfo.IsLocked())
            return Result.Fail<UnlockData>("Failed to clear lock");
    }

    return Result.Ok(new UnlockData { FilePath = filePath, WasLocked = wasLocked });
}
finally
{
    app?.Dispose(); // release COM — does NOT exit ETABS
}
```

**Data shape:**
```json
{
  "filePath": "C:\\...\\working\\model.edb",
  "wasLocked": true
}
```

---

### `generate-e2k`

Exports `.edb` → `.e2k` text format.

```bash
etab-cli generate-e2k --file <path> --output <path> [--overwrite]
```

| Flag | Required | Description |
|---|---|---|
| `--file` / `-f` | yes | Input `.edb` |
| `--output` / `-o` | no | Output `.e2k` (default: same dir as input) |
| `--overwrite` | no | Overwrite if exists |

**Connection:** Mode B always.

```csharp
ETABSApplication? app = null;
var stopwatch = Stopwatch.StartNew();
try
{
    Console.Error.WriteLine("ℹ Starting ETABS (hidden)...");
    app = ETABSWrapper.CreateNew();
    if (app is null)
        return Result.Fail<GenerateE2KData>("Failed to start ETABS");

    app.Application.Hide();
    Console.Error.WriteLine($"✓ ETABS started (hidden, v{app.FullVersion})");

    int openRet = app.Model.Files.OpenFile(filePath);
    if (openRet != 0)
        return Result.Fail<GenerateE2KData>($"OpenFile failed (ret={openRet})");

    int exportRet = app.Model.Files.ExportFile(e2kOutputPath, eFileTypeIO.TextFile);
    stopwatch.Stop();

    if (exportRet != 0 || !File.Exists(e2kOutputPath))
        return Result.Fail<GenerateE2KData>("ExportFile failed");

    return Result.Ok(new GenerateE2KData
    {
        InputFile        = filePath,
        OutputFile       = e2kOutputPath,
        FileSizeBytes    = new FileInfo(e2kOutputPath).Length,
        GenerationTimeMs = stopwatch.ElapsedMilliseconds
    });
}
finally
{
    app?.Application.ApplicationExit(false); // exit hidden instance
    app?.Dispose();
}
```

**Data shape:**
```json
{
  "inputFile": "C:\\...\\vN\\model.edb",
  "outputFile": "C:\\...\\vN\\model.e2k",
  "fileSizeBytes": 2415620,
  "generationTimeMs": 18420
}
```

---

### `extract-materials`

Extracts material takeoff → `takeoff.parquet`.

```bash
etab-cli extract-materials --file <path> --output <path>
```

**Connection:** Mode B always.

```csharp
// After OpenFile():
int numItems = 0;
string[] storyName = [], matProp = [], matType = [];
double[] dryWeight = [], volume = [];
int ret = sapModel.Results.MaterialTakeoff(
    ref numItems, ref storyName, ref matProp,
    ref matType, ref dryWeight, ref volume);

// Write Parquet (Parquet.Net 5.*)
var schema = new ParquetSchema(
    new DataField<string>("storyName"),
    new DataField<string>("materialName"),
    new DataField<string>("materialType"),
    new DataField<double>("volumeM3"),
    new DataField<double>("massKg")
);
await using var stream = File.Create(outputPath);
await using var writer = await ParquetWriter.CreateAsync(schema, stream);
await using var group = writer.CreateRowGroup();
await group.WriteColumnAsync(new DataColumn(schema.DataFields[0], storyName));
// ... remaining columns
```

**Data shape:**
```json
{
  "filePath": "C:\\...\\vN\\model.edb",
  "outputFile": "C:\\...\\vN\\materials\\takeoff.parquet",
  "rowCount": 147,
  "extractionTimeMs": 1240
}
```

---

### `run-analysis`

Runs complete analysis on a snapshot. Saves results back into the `.edb`.

```bash
etab-cli run-analysis --file <path>
```

**Connection:** Mode B always. Never attaches to user's ETABS.
**Requires `EtabSharp.Hide()` — add this before implementing.**

```csharp
ETABSApplication? app = null;
var stopwatch = Stopwatch.StartNew();
try
{
    Console.Error.WriteLine("ℹ Starting ETABS (hidden)...");
    app = ETABSWrapper.CreateNew();
    if (app is null)
        return Result.Fail<RunAnalysisData>("Failed to start ETABS");

    app.Application.Hide();
    Console.Error.WriteLine($"✓ ETABS started (hidden, v{app.FullVersion})");

    int openRet = app.Model.Files.OpenFile(filePath);
    if (openRet != 0)
        return Result.Fail<RunAnalysisData>($"OpenFile failed (ret={openRet})");

    Console.Error.WriteLine("ℹ Running analysis... (this may take several minutes)");

    // EtabSharp convenience: SetAllCasesToRun + CreateAnalysisModel + RunAnalysis
    int analysisRet = app.Model.Analyze.RunCompleteAnalysis();
    stopwatch.Stop();

    if (analysisRet != 0)
        return Result.Fail<RunAnalysisData>($"Analysis failed (ret={analysisRet})")
            with { Data = new RunAnalysisData { AnalysisTimeMs = stopwatch.ElapsedMilliseconds } };

    Console.Error.WriteLine($"✓ Analysis complete ({FormatDuration(stopwatch.Elapsed)})");

    // Save so results persist in .edb after hidden ETABS exits
    app.Model.Files.SaveFile(filePath);

    var caseStatuses = app.Model.Analyze.GetCaseStatus();
    var finished = caseStatuses.Count(cs => cs.IsFinished);

    return Result.Ok(new RunAnalysisData
    {
        FilePath = filePath,
        CaseCount = caseStatuses.Length,
        FinishedCaseCount = finished,
        AnalysisTimeMs = stopwatch.ElapsedMilliseconds
    });
}
finally
{
    app?.Application.ApplicationExit(false);
    app?.Dispose();
}
```

**Data shape:**
```json
{
  "filePath": "C:\\...\\vN\\model.edb",
  "caseCount": 12,
  "finishedCaseCount": 12,
  "analysisTimeMs": 134210
}
```

---

### `extract-results`

Extracts all 7 result tables from an analyzed `.edb` → parquet files.

```bash
etab-cli extract-results --file <path> --output-dir <path>
```

**Connection:** Mode B always.
**Requires:** `vN/model.edb` already contains analysis results.

**7 output files:**

| File | ETABS API call |
|---|---|
| `modal.parquet` | `Results.ModalParticipatingMassRatios()` |
| `base_reactions.parquet` | `Results.BaseReact()` |
| `story_forces.parquet` | `Results.StoryForces()` |
| `story_drifts.parquet` | `Results.StoryDrifts()` |
| `joint_displacements.parquet` | `Results.JointDispl()` |
| `wall_pier_forces.parquet` | `Results.PierForce()` |
| `shell_stresses.parquet` | `Results.AreaStressShell()` |

**Data shape:**
```json
{
  "filePath": "C:\\...\\vN\\model.edb",
  "outputDir": "C:\\...\\vN\\results",
  "tablesExtracted": ["modal", "base_reactions", "story_forces",
                      "story_drifts", "joint_displacements",
                      "wall_pier_forces", "shell_stresses"],
  "rowCounts": {
    "modal": 12,
    "base_reactions": 24,
    "story_forces": 360,
    "story_drifts": 720,
    "joint_displacements": 45600,
    "wall_pier_forces": 180,
    "shell_stresses": 8400
  },
  "extractionTimeMs": 8240
}
```

---

## Project Structure

```
src/EtabExtension.CLI/
  Program.cs
  Features/
    GetStatus/
      GetStatusCommand.cs
      GetStatusService.cs
      IGetStatusService.cs
      GetStatusExtensions.cs
      Models/GetStatusData.cs
    OpenModel/
      OpenModelCommand.cs
      OpenModelService.cs
      IOpenModelService.cs
      OpenModelExtensions.cs
      Models/OpenModelData.cs
    CloseModel/
      CloseModelCommand.cs
      CloseModelService.cs
      ICloseModelService.cs
      CloseModelExtensions.cs
      Models/CloseModelData.cs
    UnlockModel/
      UnlockModelCommand.cs
      UnlockModelService.cs
      IUnlockModelService.cs
      UnlockModelExtensions.cs
      Models/UnlockModelData.cs
    GenerateE2K/              ← exists, refactor to Mode B only
    ExtractMaterials/
      ExtractMaterialsCommand.cs
      ExtractMaterialsService.cs
      IExtractMaterialsService.cs
      ExtractMaterialsExtensions.cs
      Models/ExtractMaterialsData.cs
    RunAnalysis/
      RunAnalysisCommand.cs
      RunAnalysisService.cs
      IRunAnalysisService.cs
      RunAnalysisExtensions.cs
      Models/RunAnalysisData.cs
    ExtractResults/
      ExtractResultsCommand.cs
      ExtractResultsService.cs
      IExtractResultsService.cs
      ExtractResultsExtensions.cs
      Models/ExtractResultsData.cs
  Shared/
    Common/
      Result.cs
      ResultT.cs
      JsonExtensions.cs
    Infrastructure/
      Etabs/
        ComCleanup.cs         ← build this first
        EtabsExtensions.cs
        EtabsConnection/      ← keep for existing validate
        EtabsFileOperations/  ← keep
        GenerateE2KFile/      ← refactor to Mode B only
        Validation/           ← keep, add ComCleanup
        Models/
```

---

## Key Rules

**Mode A: never `ApplicationExit()`.** Release COM only. ETABS stays running.

**Mode B: always `app?.Application.ApplicationExit(false)` then `app?.Dispose()` in `finally`.**

**Never `Console.WriteLine` in a service.** Use `Console.Error.WriteLine` for progress.

**Never `Environment.Exit()` from a service.** Only command handlers exit.

**Always `Stopwatch` long operations.** Include `*TimeMs` in data shape.

---

## `.csproj` Changes

```xml
<PackageReference Include="Parquet.Net" Version="5.*" />
```

---

## Phase 1 Build Order

**Step 1 — `ComCleanup.cs`**
One file, no dependencies. Establishes the pattern before any COM code is written.

**Step 2 — `get-status`**
Tests full Mode A pipeline. Both running and not-running paths must work.
This is the eyes of Rust's state machine — highest priority.

**Step 3 — `open-model` and `close-model`**
Validates `OpenFile()` pattern and `InitializeNewModel()` (no save dialog on modified models).
Integration test both saved and unsaved models.

**Step 4 — `unlock-model`**
One COM call with a path-match guard.

**Step 5 — `generate-e2k` (refactor existing)**
Remove all Mode A logic. Add Mode B. Add `ApplicationExit(false)` in `finally`.

**Step 6 — `extract-materials`**
Requires `Parquet.Net`. Test schema against known model output.

**Step 7 — `run-analysis`**
Requires `EtabSharp.Hide()` — add to EtabSharp before this step.
Test: small model, all cases `IsFinished`, `File.SaveFile()` persists results.

**Step 8 — `extract-results`**
Implement one table at a time. Verify row counts match ETABS UI output.

---

## Testing

```
Unit tests (no ETABS):
  ├── Input validation — missing flags, wrong extensions
  ├── Result<T> serialization — success and failure shapes
  ├── ETABSApplication.Dispose() does not throw on null/disposed state
  └── JSON output matches documented data shapes

Integration tests (ETABS_INTEGRATION_TESTS=1):
  ├── get-status: running → isRunning true, PID set, version present
  ├── get-status: not running → success=true, isRunning=false
  ├── open-model: file loads, get-status confirms openFilePath
  ├── close-model --save: saves, InitializeNewModel, no file open after
  ├── close-model --no-save: no Save dialog on modified model
  ├── unlock-model: wasLocked=true after analysis, clears successfully
  ├── generate-e2k: output .e2k exists, non-empty text
  ├── extract-materials: parquet exists, rowCount > 0
  ├── run-analysis: finishedCaseCount > 0, .edb mtime updated
  └── extract-results: 7 parquet files, row counts match ETABS UI
```

```csharp
[Fact]
[Trait("Category", "Integration")]
public async Task GetStatus_WhenRunning_ReturnsRunningState()
{
    Skip.IfNot(
        Environment.GetEnvironmentVariable("ETABS_INTEGRATION_TESTS") == "1",
        "Set ETABS_INTEGRATION_TESTS=1 to run");
    // ...
}
```
