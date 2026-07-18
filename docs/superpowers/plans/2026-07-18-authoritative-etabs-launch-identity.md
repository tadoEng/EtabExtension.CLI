# Authoritative ETABS Launch Identity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace snapshot-selected managed ETABS identity with identity captured directly from a process the launcher starts and owns.

**Architecture:** Add executable discovery and owned-process/connector/clock seams beside the existing session infrastructure. `ManagedEtabsLauncher` resolves and starts ETABS, polls `ConnectToProcess` for the owned PID, transfers the process handle with the COM wrapper, and uses enumeration only for a non-authoritative stderr cross-check.

**Tech Stack:** C# 14, .NET 10, Microsoft.Extensions.Configuration, System.Diagnostics.Process, Microsoft.Win32.Registry, EtabSharp 0.3.5-beta, xUnit v3.

---

### Task 1: Resolve the ETABS executable deterministically

**Files:**
- Create: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ManagedEtabsLaunchInfrastructure.cs`
- Create: `EtabExtension.CLI.Tests/ManagedEtabsLauncherTests.cs`

- [ ] **Step 1: Write failing resolver tests**

Add tests that construct `EtabsExecutableResolver` with `ConfigurationManager` and a fake `IEtabsInstallDiscovery`:

```csharp
[Fact]
public void Explicit_missing_executable_returns_stable_not_found_error()
{
    var configuration = new ConfigurationManager
    {
        [EtabsExecutableResolver.ConfigurationKey] = @"C:\missing\ETABS.exe"
    };
    var error = Assert.Throws<EtabsLaunchException>(() =>
        new EtabsExecutableResolver(configuration, new FakeDiscovery()).Resolve());
    Assert.Equal(EtabsLaunchErrorCodes.ExecutableNotFound, error.Code);
}

[Fact]
public void Missing_config_and_discovery_returns_stable_unresolved_error()
{
    var error = Assert.Throws<EtabsLaunchException>(() =>
        new EtabsExecutableResolver(new ConfigurationManager(), new FakeDiscovery()).Resolve());
    Assert.Equal(EtabsLaunchErrorCodes.ExecutableUnresolved, error.Code);
}
```

Also prove explicit configuration wins over discovery and that registry candidates precede default-install candidates by creating temporary files for each candidate.

- [ ] **Step 2: Run the resolver tests and confirm red**

Run:

```powershell
dotnet test EtabExtension.CLI.Tests\EtabExtension.CLI.Tests.csproj --no-restore --filter ManagedEtabsLauncherTests
```

Expected: compilation fails because the resolver types do not exist.

- [ ] **Step 3: Implement resolver and discovery**

Define stable errors and the resolver contract:

```csharp
public static class EtabsLaunchErrorCodes
{
    public const string ExecutableNotFound = "ETABS_EXECUTABLE_NOT_FOUND";
    public const string ExecutableUnresolved = "ETABS_EXECUTABLE_UNRESOLVED";
    public const string ProcessStartFailed = "ETABS_PROCESS_START_FAILED";
    public const string ProcessIdentityFailed = "ETABS_PROCESS_IDENTITY_FAILED";
    public const string AttachTimeout = "ETABS_ATTACH_TIMEOUT";
}

public sealed class EtabsLaunchException(string code, string message, Exception? inner = null)
    : InvalidOperationException($"[{code}] {message}", inner)
{
    public string Code { get; } = code;
}

public interface IEtabsInstallDiscovery
{
    IReadOnlyList<string> RegistryCandidates();
    IReadOnlyList<string> DefaultInstallCandidates();
}
```

`EtabsExecutableResolver.Resolve()` must use `EtabsExePath`, then registry candidates, then default candidates. An explicit missing path fails immediately. `WindowsEtabsInstallDiscovery` reads ETABS uninstall entries from HKLM/HKCU, including the 32-bit HKLM view, then enumerates `%ProgramFiles%\Computers and Structures\ETABS *\ETABS.exe`; each group is ordered newest-first.

- [ ] **Step 4: Run resolver tests and confirm green**

Run the same filtered command. Expected: all resolver tests pass without starting ETABS.

- [ ] **Step 5: Commit executable resolution**

```powershell
git add src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ManagedEtabsLaunchInfrastructure.cs EtabExtension.CLI.Tests/ManagedEtabsLauncherTests.cs
git commit -m "feat: resolve managed ETABS executable"
```

### Task 2: Launch an owned process and attach only to its PID

**Files:**
- Modify: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ManagedEtabsLaunchInfrastructure.cs`
- Modify: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/SessionIdentity.cs`
- Modify: `EtabExtension.CLI.Tests/ManagedEtabsLauncherTests.cs`

- [ ] **Step 1: Write failing authoritative-identity tests**

Use fake `IOwnedEtabsProcess`, `IEtabsProcessStarter`, `IManagedEtabsConnector`, `IEtabsLaunchClock`, and `IProcessInspector` implementations to prove:

```csharp
[Fact]
public void Launch_records_owned_identity_even_when_foreign_etabs_appears()
{
    var owned = Identity(pid: 42);
    var foreign = Identity(pid: 99);
    var launcher = CreateLauncher(owned, snapshots: [[], [owned, foreign]]);

    var result = launcher.Launch();

    Assert.Equal(owned, result.Identity);
    Assert.All(_connector.RequestedPids, pid => Assert.Equal(42, pid));
}

[Fact]
public void Attach_timeout_kills_only_the_owned_process()
{
    var launcher = CreateLauncher(Identity(pid: 42), connectorAlwaysNotReady: true);

    var error = Assert.Throws<EtabsLaunchException>(() => launcher.Launch());

    Assert.Equal(EtabsLaunchErrorCodes.AttachTimeout, error.Code);
    Assert.Equal(1, _owned.KillCount);
    Assert.DoesNotContain(99, _processes.TerminatedPids);
}
```

Add assertions for retry count/PID, disposal, guarded hide behavior through the connector fake, and the exact cross-check warning text.

- [ ] **Step 2: Run launcher tests and confirm red**

Run:

```powershell
dotnet test EtabExtension.CLI.Tests\EtabExtension.CLI.Tests.csproj --no-restore --filter ManagedEtabsLauncherTests
```

Expected: new launcher tests fail because `ManagedEtabsLauncher` still uses snapshot selection.

- [ ] **Step 3: Implement owned process and connector seams**

Add:

```csharp
public interface IOwnedEtabsProcess : IDisposable
{
    ManagedProcessIdentity Identity { get; }
    bool HasExited { get; }
    void Kill();
    bool WaitForExit(TimeSpan timeout);
}

public interface IEtabsProcessStarter
{
    IOwnedEtabsProcess Start(string executablePath);
}

public interface IManagedEtabsConnector
{
    IManagedEtabsApplication? TryConnect(
        IOwnedEtabsProcess process,
        Guid launchRecordId,
        out string? error);
}

public interface IEtabsLaunchClock
{
    DateTimeOffset UtcNow { get; }
    void Sleep(TimeSpan duration);
}
```

`WindowsEtabsProcessStarter` calls `Process.Start` with `UseShellExecute=false` and captures PID, UTC start time, and `MainModule.FileName` directly. `EtabSharpManagedEtabsConnector` calls `ETABSWrapper.ConnectToProcess(process.Identity.Pid)`, disposes partial wrappers on failure, and guards `Hide()` with `Visible()`.

- [ ] **Step 4: Replace snapshot selection in `ManagedEtabsLauncher`**

The production constructor receives `IConfiguration` from DI and creates production resolver/starter/connector/clock dependencies. A seam constructor accepts fakes. Use these constants:

```csharp
public static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(60);
public static readonly TimeSpan AttachRetryInterval = TimeSpan.FromMilliseconds(100);
```

Add this production marker immediately above the polling loop:

```csharp
// TODO(issue #238 live certification): Verify that a plainly started ETABS.exe
// accepts ConnectToProcess before any model is open and measure readiness latency.
```

On success, compare the after-minus-before snapshot PID set with the owned PID only for diagnostics. On disagreement write exactly:

```csharp
Console.Error.WriteLine(
    $"⚠ Managed ETABS launch cross-check disagreed with authoritative owned PID {ownedPid}: " +
    $"snapshot candidates=[{candidateText}]. Authoritative owned-process identity retained.");
```

On any failure before transfer, kill, wait for, and dispose only the owned process.

- [ ] **Step 5: Run launcher and existing session tests**

Run:

```powershell
dotnet test EtabExtension.CLI.Tests\EtabExtension.CLI.Tests.csproj --no-restore --filter "ManagedEtabsLauncherTests|ManagedSessionTests"
```

Expected: all launcher/session tests pass; no real ETABS starts.

- [ ] **Step 6: Commit authoritative launching**

```powershell
git add src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ManagedEtabsLaunchInfrastructure.cs src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/SessionIdentity.cs EtabExtension.CLI.Tests/ManagedEtabsLauncherTests.cs
git commit -m "fix: derive managed identity from owned process"
```

### Task 3: Full verification and handoff

**Files:**
- Verify only; do not change serve inspection files.

- [ ] **Step 1: Run full xUnit**

```powershell
dotnet test EtabExtension.CLI.Tests\EtabExtension.CLI.Tests.csproj --no-restore
```

Expected: every test passes with zero failures and no ETABS process launched.

- [ ] **Step 2: Run full solution build**

```powershell
dotnet build EtabExtension.CLI.slnx --no-restore --nologo
```

Expected: build succeeds with zero errors; pre-existing analyzer/NuGet warnings are reported separately.

- [ ] **Step 3: Audit branch scope**

```powershell
git diff --check
git status --short --branch
git diff --name-only e4d756a..HEAD
git log --oneline e4d756a..HEAD
```

Expected: only design/plan docs, launcher/session infrastructure, and launcher tests changed; no #243 inspection files; no push or merge.
