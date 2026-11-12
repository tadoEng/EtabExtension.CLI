using System.Text.Json;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

// WP-C0 (core-loop §2.5-2.6): version pre-check and modal-dialog watchdog. ETABS-free:
// the window station is behind IEtabsDialogWindowSource.
public sealed class ModalDialogAndVersionTests
{
    // ── Version pre-check (§2.5) ───────────────────────────────────────────────

    [Theory]
    [InlineData("23.3.0", "23.2.0")]
    [InlineData("24.0.0", "23.9.1")]
    [InlineData("23.3", "23.2.0")]
    public void ANewerModelIsATypedMismatch(string model, string installed)
    {
        var failure = EtabsModelVersionCheck.Check(model, installed);

        Assert.NotNull(failure);
        Assert.StartsWith("[ETABS_VERSION_MISMATCH] ", failure, StringComparison.Ordinal);
        var detail = DetailOf(failure);
        Assert.Equal(EtabsVersion.TryParse(model, out var m) ? m.ToString() : null, detail.GetProperty("model").GetString());
        Assert.Equal(installed, detail.GetProperty("installed").GetString());
    }

    /// <summary>Mutation guard for "compare ignoring minor": 23.3 vs 23.2 is the DD-5 case.</summary>
    [Theory]
    [InlineData("23.2.0", "23.2.0")]
    [InlineData("23.2.9", "23.2.0")]
    [InlineData("23.1.0", "23.2.0")]
    [InlineData("22.7.0", "23.2.0")]
    public void AnEqualOrOlderModelOpens(string model, string installed) =>
        Assert.Null(EtabsModelVersionCheck.Check(model, installed));

    [Fact]
    public void NoStatedVersionMeansNoCheck() =>
        Assert.Null(EtabsModelVersionCheck.Check(null, "23.2.0"));

    [Fact]
    public void AnUnreadableStatedVersionRefusesWithoutAMismatchCode()
    {
        var failure = EtabsModelVersionCheck.Check("twenty-three", "23.2.0");

        Assert.NotNull(failure);
        Assert.DoesNotContain("ETABS_VERSION_MISMATCH", failure, StringComparison.Ordinal);
    }

    // ── Modal-dialog watchdog (§2.6) ───────────────────────────────────────────

    private const string OlderVersionText =
        "This model was created with a newer version of ETABS and cannot be opened in an older version.";

    [Fact]
    public async Task AnUnknownDialogFailsTheCommandAndIsLeftOpen()
    {
        var release = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = new FakeDialogs { Dialog = new(1, "ETABS", ["Unexpected error 42"]) };
        var watchdog = Watchdog(windows);

        var result = Assert.IsType<Result>(await watchdog.GuardAsync(
            () => 4242, () => release.Task, TestContext.Current.CancellationToken));

        Assert.False(result.Success);
        Assert.StartsWith("[ETABS_MODAL_DIALOG] ", result.Error, StringComparison.Ordinal);
        var detail = DetailOf(result.Error!);
        Assert.Equal("ETABS", detail.GetProperty("title").GetString());
        Assert.Equal("Unexpected error 42", detail.GetProperty("text").GetString());
        Assert.False(detail.GetProperty("dismissed").GetBoolean());
        Assert.Equal(0, windows.DismissCalls);
        Assert.Equal([4242], windows.ScannedPids.Distinct());

        // The work is still stuck behind the dialog: later commands are refused, typed.
        Assert.True(watchdog.IsBlocked);
        Assert.StartsWith("[ETABS_MODAL_DIALOG] ", watchdog.BlockedFailure().Error, StringComparison.Ordinal);
        release.SetResult(Result.Ok());
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(watchdog.IsBlocked);
    }

    [Fact]
    public async Task TheKnownOlderVersionBoxIsDismissedAndStillFailsTheCommand()
    {
        var release = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = new FakeDialogs
        {
            Dialog = new(1, "ETABS", [OlderVersionText]),
            OnDismiss = () => release.SetResult(Result.Fail("OpenFile returned 1"))
        };

        var result = Assert.IsType<Result>(await Watchdog(windows).GuardAsync(
            () => 4242, () => release.Task, TestContext.Current.CancellationToken));

        Assert.False(result.Success);
        Assert.True(DetailOf(result.Error!).GetProperty("dismissed").GetBoolean());
        Assert.Equal(1, windows.DismissCalls);
    }

    /// <summary>Mutation guard: a dialog seen while the work succeeded is never a success.</summary>
    [Fact]
    public async Task ADialogSeenAfterTheWorkReturnedSuccessStillFailsTheCommand()
    {
        var windows = new FakeDialogs { Dialog = new(1, "Warning", ["Something happened"]) };

        var result = Assert.IsType<Result>(await Watchdog(windows, TimeSpan.FromSeconds(30)).GuardAsync(
            () => 4242,
            () => Task.FromResult<object>(Result.Ok()),
            TestContext.Current.CancellationToken));

        Assert.False(result.Success);
        Assert.StartsWith("[ETABS_MODAL_DIALOG] ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutADialogTheWorkResultPassesThrough()
    {
        var windows = new FakeDialogs();
        var expected = Result.Ok();

        var result = await Watchdog(windows).GuardAsync(
            () => 4242,
            async () =>
            {
                await Task.Delay(30);
                return expected;
            },
            TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.NotEmpty(windows.ScannedPids);
    }

    [Fact]
    public async Task NoOwnedProcessMeansNothingIsScanned()
    {
        var windows = new FakeDialogs { Dialog = new(1, "ETABS", ["x"]) };
        var expected = Result.Ok();

        var result = await Watchdog(windows).GuardAsync(
            () => null,
            () => Task.FromResult<object>(expected),
            TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.Empty(windows.ScannedPids);
    }

    /// <summary>
    /// Card W3 (replaces P3-4(a)'s "pass through"): a fault in the window source never faults
    /// the watch, but it is not "nothing found" either. The command is failed typed and
    /// retryable - success is never answered over a scan that could not be taken.
    /// </summary>
    [Fact]
    public async Task InconclusiveScanNeverAnswersSuccess()
    {
        var windows = new FakeDialogs { ThrowOnFind = true };
        var expected = Result.Ok();

        var result = await Watchdog(windows).GuardAsync(
            () => 4242,
            async () =>
            {
                await Task.Delay(30);
                return expected;
            },
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<Result>(result);
        Assert.False(failure.Success);
        Assert.NotSame(expected, result);
        Assert.StartsWith(
            $"[{EtabsTypedFailureCodes.DialogScanInconclusive}]",
            failure.Error,
            StringComparison.Ordinal);
        Assert.Contains("detail={\"reason\":", failure.Error, StringComparison.Ordinal);
        Assert.NotEmpty(windows.ScannedPids);
    }

    /// <summary>A scan that hangs is bounded by the scan timeout and is inconclusive too.</summary>
    [Fact]
    public async Task AScanThatNeverReturnsIsInconclusiveWithinTheScanTimeout()
    {
        using var release = new ManualResetEventSlim(false);
        var windows = new FakeDialogs { BlockOnFind = release };
        var watchdog = new EtabsModalDialogWatchdog(
            windows,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(100));

        try
        {
            var guarded = watchdog.GuardAsync(
                () => 4242,
                async () =>
                {
                    await Task.Delay(30);
                    return Result.Ok();
                },
                TestContext.Current.CancellationToken);
            var result = await guarded.WaitAsync(Kit.TestDeadline);

            Assert.False(Assert.IsType<Result>(result).Success);
            Assert.Contains(
                EtabsTypedFailureCodes.DialogScanInconclusive,
                ((Result)result).Error!,
                StringComparison.Ordinal);
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>The scan being clean on the final look still answers the work's result.</summary>
    [Fact]
    public async Task ACleanFinalScanStillPassesTheWorkResultThrough()
    {
        var expected = Result.Ok();

        var result = await Watchdog(new FakeDialogs()).GuardAsync(
            () => 4242,
            async () =>
            {
                await Task.Delay(30);
                return expected;
            },
            TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
    }

    /// <summary>
    /// P3-4(a): a dismiss that throws on a dialog that IS there neither faults the watch nor
    /// hides the dialog: the command still fails typed, with the box reported as left open.
    /// </summary>
    [Fact]
    public async Task AThrowingDismissStillFailsTypedWithTheDialogLeftOpen()
    {
        var release = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = new FakeDialogs
        {
            Dialog = new(1, "ETABS", [OlderVersionText]),
            ThrowOnDismiss = true
        };
        var watchdog = Watchdog(windows);

        try
        {
            var result = Assert.IsType<Result>(await watchdog.GuardAsync(
                () => 4242, () => release.Task, TestContext.Current.CancellationToken));

            Assert.False(result.Success);
            Assert.StartsWith("[ETABS_MODAL_DIALOG] ", result.Error, StringComparison.Ordinal);
            Assert.False(DetailOf(result.Error!).GetProperty("dismissed").GetBoolean());
            Assert.Equal(1, windows.DismissCalls);
            Assert.True(watchdog.IsBlocked);
        }
        finally
        {
            release.SetResult(Result.Ok());
        }
    }

    /// <summary>
    /// P3-4(b): the known box whose dismiss did not take (the window survived) is still a
    /// typed failure, reported dismissed:false, and the session stays blocked.
    /// </summary>
    [Fact]
    public async Task AKnownBoxThatCouldNotBeDismissedFailsTypedAsLeftOpen()
    {
        var release = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = new FakeDialogs
        {
            Dialog = new(1, "ETABS", [OlderVersionText]),
            DismissResult = false
        };
        var watchdog = Watchdog(windows);

        try
        {
            var result = Assert.IsType<Result>(await watchdog.GuardAsync(
                () => 4242, () => release.Task, TestContext.Current.CancellationToken));

            Assert.False(result.Success);
            Assert.StartsWith("[ETABS_MODAL_DIALOG] ", result.Error, StringComparison.Ordinal);
            Assert.Contains("it was left open", result.Error, StringComparison.Ordinal);
            Assert.False(DetailOf(result.Error!).GetProperty("dismissed").GetBoolean());
            Assert.Equal(1, windows.DismissCalls);
            Assert.True(watchdog.IsBlocked);
            Assert.Equal(result.Error, watchdog.BlockedFailure().Error);
        }
        finally
        {
            release.SetResult(Result.Ok());
        }
    }

    // ── P3-5: cross-repo parity with docs/contracts/sidecar-v1/failures.jsonl ──
    //
    // These literals are copied from the EtabExtension repo's failures.jsonl (ids 21-23),
    // which the Rust decoder is tested against. If the C# prose or detail shape drifts, this
    // fails here instead of in the desktop's decoder.

    private const string Fixture21 =
        "[ETABS_MODAL_DIALOG] ETABS is waiting on a modal dialog; it was left open and the command failed. " +
        "The ETABS session is busy until the dialog is answered. " +
        "detail={\"title\":\"ETABS\",\"text\":\"Unexpected error in the model file.\",\"dismissed\":false}";

    private const string Fixture22 =
        "Operation failed: [ETABS_MODAL_DIALOG] ETABS showed a modal dialog it cannot continue past; " +
        "the dialog was closed and the command failed. " +
        "detail={\"title\":\"ETABS\",\"text\":\"This file cannot be opened in an older version of ETABS.\",\"dismissed\":true}";

    private const string Fixture23 =
        "[ETABS_VERSION_MISMATCH] The model was saved by ETABS 23.3.0, which is newer than the running " +
        "ETABS 23.2.0; it was not opened. detail={\"model\":\"23.3.0\",\"installed\":\"23.2.0\"}";

    [Fact]
    public void TheDetailMarkerMatchesTheContract() =>
        Assert.Equal(" detail=", EtabsTypedFailureCodes.DetailMarker);

    [Fact]
    public void ALeftOpenDialogFailureMatchesContractFixture21() =>
        Assert.Equal(
            Fixture21,
            new EtabsModalDialogObservation("ETABS", "Unexpected error in the model file.", false).ToFailure());

    /// <summary>Fixture 22 is the operation lane's form: its prefix, then the typed failure.</summary>
    [Fact]
    public void ADismissedDialogFailureMatchesContractFixture22() =>
        Assert.Equal(
            Fixture22,
            "Operation failed: " + new EtabsModalDialogObservation(
                "ETABS",
                "This file cannot be opened in an older version of ETABS.",
                true).ToFailure());

    [Fact]
    public void AVersionMismatchFailureMatchesContractFixture23() =>
        Assert.Equal(Fixture23, EtabsModelVersionCheck.Check("23.3.0", "23.2.0"));

    /// <summary>The real ETABS 23.2 box (live, 2026-11-11): #32770, title "ETABS", one OK.</summary>
    [Fact]
    public void TheRealEtabs232OlderVersionBoxIsKnown() =>
        Assert.True(KnownEtabsDialogs.IsOlderVersionBox(new(
            1,
            "ETABS",
            [
                "Version 23.3.0 file cannot be opened in older Version 23.2.0.",
                "You may be able to import the corresponding (.e2k) text file."
            ])));

    [Theory]
    [InlineData("ETABS", OlderVersionText, true)]
    [InlineData("Cannot be opened in older Version", "", true)]
    [InlineData("ETABS", "The file cannot be opened.", false)]
    [InlineData("ETABS", "Analysis complete", false)]
    public void OnlyTheOlderVersionBoxIsKnown(string title, string text, bool known) =>
        Assert.Equal(known, KnownEtabsDialogs.IsOlderVersionBox(new(1, title, [text])));

    private static EtabsModalDialogWatchdog Watchdog(
        FakeDialogs windows,
        TimeSpan? settle = null) =>
        new(windows, TimeSpan.FromMilliseconds(5), settle ?? TimeSpan.FromSeconds(5));

    private static JsonElement DetailOf(string failure)
    {
        var marker = failure.IndexOf(" detail=", StringComparison.Ordinal);
        Assert.True(marker > 0, failure);
        return JsonSerializer.Deserialize<JsonElement>(failure[(marker + " detail=".Length)..]);
    }

    private sealed class FakeDialogs : IEtabsDialogWindowSource
    {
        private readonly object _gate = new();
        private readonly List<int> _scanned = [];
        private bool _dismissed;

        public EtabsDialogWindow? Dialog { get; init; }
        public Action? OnDismiss { get; init; }
        public bool ThrowOnFind { get; init; }
        public bool ThrowOnDismiss { get; init; }
        public ManualResetEventSlim? BlockOnFind { get; init; }

        /// <summary>False models a dismiss that did not take: the window is still there.</summary>
        public bool DismissResult { get; init; } = true;

        public int DismissCalls { get; private set; }

        public IReadOnlyList<int> ScannedPids
        {
            get
            {
                lock (_gate)
                {
                    return _scanned.ToList();
                }
            }
        }

        public IReadOnlyList<EtabsDialogWindow> FindModalDialogs(int pid)
        {
            BlockOnFind?.Wait(TimeSpan.FromSeconds(30));
            lock (_gate)
            {
                _scanned.Add(pid);
                if (ThrowOnFind)
                {
                    throw new InvalidOperationException("window walk failed");
                }

                return Dialog is null || _dismissed ? [] : [Dialog];
            }
        }

        public bool TryDismiss(EtabsDialogWindow dialog)
        {
            lock (_gate)
            {
                DismissCalls++;
                if (ThrowOnDismiss)
                {
                    throw new InvalidOperationException("PostMessage failed");
                }

                _dismissed = DismissResult;
            }

            OnDismiss?.Invoke();
            return DismissResult;
        }
    }
}
