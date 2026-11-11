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
            lock (_gate)
            {
                _scanned.Add(pid);
                return Dialog is null || _dismissed ? [] : [Dialog];
            }
        }

        public bool TryDismiss(EtabsDialogWindow dialog)
        {
            lock (_gate)
            {
                DismissCalls++;
                _dismissed = true;
            }

            OnDismiss?.Invoke();
            return true;
        }
    }
}
