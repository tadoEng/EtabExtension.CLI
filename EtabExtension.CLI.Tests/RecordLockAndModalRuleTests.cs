using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>Per-daemon record directory, <c>daemon.lock</c>, and recovery only under the lock (card V3).</summary>
public sealed class RecordLockTests : IDisposable
{
    private static readonly ManagedProcessIdentity Identity = Kit.Identity;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "c747-" + Guid.NewGuid().ToString("N"));

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    private string RolePath(string role = "editing", string file = "record.json") =>
        Path.Combine(_root, "etabs-session", role, file);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                // A junction must be removed without following it.
                foreach (var junction in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                    .Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                    .ToList())
                {
                    Directory.Delete(junction, recursive: false);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort: a leftover temp folder is not a test failure
        }
    }

    private static ManagedEtabsSessionRecord Record(
        ManagedProcessIdentity identity,
        ManagedEtabsRecordPhase? phase = ManagedEtabsRecordPhase.Provisional) => new(
        1,
        identity.Pid,
        identity.ProcessStartTimeUtc,
        identity.ExecutablePath,
        Guid.NewGuid(),
        DateTimeOffset.UtcNow)
    {
        Phase = phase
    };

    // The card's SecondDaemonOnTheSameRoleDirectoryGetsRecordInUse, and A2: a second live
    // daemon on the same (or an aliased) record path cannot recover, overwrite or clear the
    // first's record.
    [Fact]
    public void SecondDaemonOnTheSameRoleDirectoryGetsRecordInUse()
    {
        using var first = new JsonSessionRecordStore(RolePath(), Short);
        first.AcquireExclusive();
        first.Write(Record(Identity));
        var before = File.ReadAllText(RolePath());

        // The same path, an aliased spelling of it, and another file name in the directory.
        foreach (var path in new[]
        {
            RolePath(),
            Path.Combine(_root, "etabs-session", "editing", ".", "record.json"),
            RolePath(file: "other.json")
        })
        {
            using var second = new JsonSessionRecordStore(path, Short);
            var error = Assert.Throws<RecordInUseException>(second.AcquireExclusive);
            Assert.Contains(DaemonRecordLock.LockFileName, error.RecordPath, StringComparison.Ordinal);
            Assert.False(second.HoldsExclusiveLock);

            // Without the lock it cannot write or clear.
            Assert.Throws<InvalidOperationException>(() => second.Write(Record(Identity with { Pid = 9 })));
            Assert.Throws<InvalidOperationException>(second.Clear);
        }

        Assert.Equal(before, File.ReadAllText(RolePath()));
    }

    [Fact]
    public void ADifferentRoleDirectoryDoesNotContendAndTheLockIsReleasedOnDispose()
    {
        using var editing = new JsonSessionRecordStore(RolePath("editing"), Short);
        using var analysis = new JsonSessionRecordStore(RolePath("analysis"), Short);
        editing.AcquireExclusive();
        analysis.AcquireExclusive();

        editing.Dispose();
        using var successor = new JsonSessionRecordStore(RolePath("editing"), Short);
        successor.AcquireExclusive();

        Assert.True(successor.HoldsExclusiveLock);
        Assert.True(analysis.HoldsExclusiveLock);
    }

    [Fact]
    public void ReadingNeedsNoLockSoTheSiblingCanObserveTheRecord()
    {
        using var owner = new JsonSessionRecordStore(RolePath(), Short);
        owner.AcquireExclusive();
        var record = Record(Identity);
        owner.Write(record);

        using var reader = new JsonSessionRecordStore(RolePath(), Short);

        Assert.Equal(record, reader.Read());
    }

    [Fact]
    public void ThePhaseIsOptionalOnTheWireAndOmittedWhenAbsent()
    {
        using var store = new JsonSessionRecordStore(RolePath(), Short);
        store.AcquireExclusive();

        store.Write(Record(Identity, ManagedEtabsRecordPhase.Provisional));
        var provisional = JsonDocument.Parse(File.ReadAllText(RolePath())).RootElement;
        Assert.Equal("provisional", provisional.GetProperty("phase").GetString());

        store.Write(Record(Identity, phase: null));
        var legacy = JsonDocument.Parse(File.ReadAllText(RolePath())).RootElement;
        Assert.False(legacy.TryGetProperty("phase", out _));
        Assert.Null(store.Read()!.Phase);
    }

    [Fact]
    public void RecordLockRejectsHardLinkedRecordAndJunctionedDirectory()
    {
        // 1. A record file with a second hard link.
        var plain = RolePath("hardlink");
        Directory.CreateDirectory(Path.GetDirectoryName(plain)!);
        File.WriteAllText(plain, "{}");
        Assert.True(CreateHardLinkW(Path.Combine(_root, "alias.json"), plain, nint.Zero));
        using (var store = new JsonSessionRecordStore(plain, Short))
        {
            var unsafePath = Assert.Throws<RecordPathUnsafeException>(store.AcquireExclusive);
            Assert.Contains("hard link", unsafePath.Message, StringComparison.Ordinal);
            Assert.False(store.HoldsExclusiveLock);
        }

        // 2. A hard-linked lock file.
        var linkedLock = RolePath("linkedlock");
        Directory.CreateDirectory(Path.GetDirectoryName(linkedLock)!);
        var lockPath = Path.Combine(Path.GetDirectoryName(linkedLock)!, DaemonRecordLock.LockFileName);
        File.WriteAllText(lockPath, string.Empty);
        Assert.True(CreateHardLinkW(Path.Combine(_root, "alias.lock"), lockPath, nint.Zero));
        using (var store = new JsonSessionRecordStore(linkedLock, Short))
        {
            Assert.Throws<RecordPathUnsafeException>(store.AcquireExclusive);
        }

        // 3. A junctioned directory.
        var target = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(target);
        var junction = Path.Combine(_root, "junctioned");
        RunCmd($"mklink /J \"{junction}\" \"{target}\"");
        Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);
        using (var store = new JsonSessionRecordStore(Path.Combine(junction, "record.json"), Short))
        {
            var unsafePath = Assert.Throws<RecordPathUnsafeException>(store.AcquireExclusive);
            Assert.Contains("reparse point", unsafePath.Message, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(Path.Combine(target, DaemonRecordLock.LockFileName)));
    }

    // ── recovery runs only while holding the lock (card V3) ────────────────

    [Fact]
    public void OrphanRecoveryRunsOnlyWhileHoldingTheLock()
    {
        using var owner = new JsonSessionRecordStore(RolePath(), Short);
        owner.AcquireExclusive();
        owner.Write(Record(Identity));
        var processes = new ExactProcesses(Identity);

        using var intruder = new JsonSessionRecordStore(RolePath(), Short);
        var result = new OrphanSessionCleaner(intruder, processes).Clean();

        Assert.False(result.Success);
        Assert.Equal(EtabsRecordErrorCodes.RecordInUse, result.ErrorCode);
        Assert.True(result.Data.RecordRetained);
        Assert.Equal(0, processes.TerminateCalls);
        Assert.NotNull(owner.Read());
        Assert.True(processes.IsAlive(Identity.Pid));
    }

    [Fact]
    public void ARecordPathThatIsNotPlainFailsRecoveryTypedAndTouchesNothing()
    {
        var target = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(target);
        var junction = Path.Combine(_root, "junctioned");
        RunCmd($"mklink /J \"{junction}\" \"{target}\"");
        var processes = new ExactProcesses(Identity);

        using var store = new JsonSessionRecordStore(Path.Combine(junction, "record.json"), Short);
        var result = new OrphanSessionCleaner(store, processes).Clean();

        Assert.False(result.Success);
        Assert.Equal(EtabsRecordErrorCodes.RecordPathUnsafe, result.ErrorCode);
        Assert.Equal(0, processes.TerminateCalls);
    }

    [Fact]
    public void StaleProvisionalRecordIsKilledByExactIdentityAtOwnStart()
    {
        using var store = new JsonSessionRecordStore(RolePath(), Short);
        store.AcquireExclusive();
        store.Write(Record(Identity));
        var processes = new ExactProcesses(Identity);

        var result = new OrphanSessionCleaner(store, processes).Clean();

        Assert.True(result.Success);
        Assert.True(result.Data.Forced);
        Assert.Equal(1, processes.TerminateCalls);
        Assert.False(processes.IsAlive(Identity.Pid));
        Assert.Null(store.Read());
    }

    // A8 test 7: start-time and exe-path mismatches, in termination as well as classification.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AReusedPidWithADifferentStartTimeOrPathIsNeverTerminated(
        bool differentStart,
        bool differentPath)
    {
        using var store = new JsonSessionRecordStore(RolePath(), Short);
        store.AcquireExclusive();
        store.Write(Record(Identity));
        var reused = Identity with
        {
            ProcessStartTimeUtc = differentStart ? Identity.ProcessStartTimeUtc.AddSeconds(5) : Identity.ProcessStartTimeUtc,
            ExecutablePath = differentPath ? @"C:\Other\ETABS.exe" : Identity.ExecutablePath
        };
        var processes = new ExactProcesses(reused);

        var result = new OrphanSessionCleaner(store, processes).Clean();

        Assert.False(result.Success);
        Assert.Equal(ManagedEtabsShutdownState.IdentityMismatch, result.Data.State);
        Assert.True(result.Data.RecordRetained);
        Assert.True(processes.IsAlive(reused.Pid));
        Assert.NotNull(store.Read());
    }

    [Fact]
    public void AStaleRecordWhoseProcessIsGoneIsClearedWithoutKillingAnything()
    {
        using var store = new JsonSessionRecordStore(RolePath(), Short);
        store.AcquireExclusive();
        store.Write(Record(Identity));
        var processes = new ExactProcesses();

        var result = new OrphanSessionCleaner(store, processes).Clean();

        Assert.True(result.Success);
        Assert.False(result.Data.Forced);
        Assert.Null(store.Read());
    }

    private static void RunCmd(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c {arguments}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, nint securityAttributes);

    /// <summary>Live processes with the real exact-identity semantics of <c>TerminateExact</c>.</summary>
    private sealed class ExactProcesses : IProcessInspector
    {
        private readonly List<ManagedProcessIdentity> _live;

        public ExactProcesses(params ManagedProcessIdentity[] live) => _live = [.. live];

        public int TerminateCalls { get; private set; }

        public bool IsAlive(int pid) => _live.Any(identity => identity.Pid == pid);

        public EtabsProcessObservation ObserveEtabs() => new(_live.ToList(), 0);

        public ManagedProcessIdentity? Find(int pid) => _live.FirstOrDefault(identity => identity.Pid == pid);

        public IOwnedEtabsProcess? OpenExact(ManagedProcessIdentity expected) => null;

        public ExactProcessTerminationResult TerminateExact(ManagedProcessIdentity expected, TimeSpan timeout)
        {
            var live = _live.FirstOrDefault(identity => identity.Pid == expected.Pid);
            if (live is null)
            {
                return new(ExactProcessTerminationState.NotFound, null);
            }

            if (live.ProcessStartTimeUtc != expected.ProcessStartTimeUtc
                || !string.Equals(live.ExecutablePath, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                return new(ExactProcessTerminationState.IdentityMismatchOrUnidentified, live);
            }

            TerminateCalls++;
            _live.Remove(live);
            return new(ExactProcessTerminationState.ConfirmedGone, live);
        }
    }
}

/// <summary>The modal rule (card V2, W3) as a pure function over window facts.</summary>
public sealed class ModalRuleTests
{
    private static EtabsWindowFacts Dialog(nint owner, bool ownerEnabled, string windowClass = "#32770") =>
        new(0x10, windowClass, owner, ownerEnabled);

    [Fact]
    public void ModelessDialogOnAnEnabledOwnerIsNotModal() =>
        Assert.False(EtabsModalDialogRule.IsModalCandidate(Dialog(owner: 0x1, ownerEnabled: true)));

    [Fact]
    public void ADialogOwnedByADisabledOwnerIsACandidateWhetherOrNotItIsVisible() =>
        // Visibility is not even an input: a guard-hidden modal box is still modal.
        Assert.True(EtabsModalDialogRule.IsModalCandidate(Dialog(owner: 0x1, ownerEnabled: false)));

    [Fact]
    public void OwnerlessDialogIsACandidate() =>
        Assert.True(EtabsModalDialogRule.IsModalCandidate(Dialog(owner: 0, ownerEnabled: true)));

    [Theory]
    [InlineData("Static")]
    [InlineData("HwndWrapper[ETABS.exe]")]
    [InlineData("AuthenticationView")]
    public void OnlyTheDialogClassIsEverACandidate(string windowClass) =>
        Assert.False(EtabsModalDialogRule.IsModalCandidate(Dialog(owner: 0x1, ownerEnabled: false, windowClass)));

    // Competing popups: of several dialogs, only those that block a DISABLED owner qualify.
    [Fact]
    public void OfCompetingDialogsOnlyTheOnesBlockingADisabledOwnerAreCandidates()
    {
        EtabsWindowFacts[] windows =
        [
            new(0x11, "#32770", 0x1, OwnerEnabled: true),   // modeless box on the enabled main window
            new(0x12, "#32770", 0x2, OwnerEnabled: false),  // a real modal box
            new(0x13, "#32770", 0x1, OwnerEnabled: true)    // another modeless box
        ];

        var candidates = windows.Where(EtabsModalDialogRule.IsModalCandidate).Select(w => w.Handle).ToArray();

        Assert.Equal([(nint)0x12], candidates);
    }

    // Nested owners: dialog B is owned by dialog A, which is owned by the (disabled) main
    // window. Each is judged by its DIRECT owner; the box on top (B) blocks A.
    [Fact]
    public void NestedOwnersAreJudgedByTheirDirectOwner()
    {
        var a = new EtabsWindowFacts(0xA, "#32770", 0x1, OwnerEnabled: false);
        var b = new EtabsWindowFacts(0xB, "#32770", 0xA, OwnerEnabled: false);
        var c = new EtabsWindowFacts(0xC, "#32770", 0xA, OwnerEnabled: true);

        Assert.True(EtabsModalDialogRule.IsModalCandidate(a));
        Assert.True(EtabsModalDialogRule.IsModalCandidate(b));
        Assert.False(EtabsModalDialogRule.IsModalCandidate(c));
    }

    // W3: class and title never exempt a dialog. An ownerless modal and an ownerless
    // modeless box with identical class and title are BOTH candidates.
    [Fact]
    public void IdenticalClassAndTitleForAModalAndAModelessDialogAreBothCandidates()
    {
        var modal = new EtabsWindowFacts(0x21, "#32770", nint.Zero, OwnerEnabled: true);
        var modeless = new EtabsWindowFacts(0x22, "#32770", nint.Zero, OwnerEnabled: true);

        Assert.True(EtabsModalDialogRule.IsModalCandidate(modal));
        Assert.True(EtabsModalDialogRule.IsModalCandidate(modeless));
    }

    [Fact]
    public void TheRuleHasNoVisibilityInputAndNoAllowlist()
    {
        var inputs = typeof(EtabsWindowFacts).GetProperties().Select(property => property.Name).Order().ToArray();

        Assert.Equal(["ClassName", "Handle", "Owner", "OwnerEnabled"], inputs);
    }
}
