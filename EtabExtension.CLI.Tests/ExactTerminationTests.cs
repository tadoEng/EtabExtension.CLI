using System.Diagnostics;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// The real exact-identity termination and classification, against a harmless sacrificial
/// child process (<c>ping</c>) - never ETABS. A8 test 7: start-time and exe-path mismatches,
/// in BOTH classification and termination.
/// </summary>
public sealed class ExactTerminationTests
{
    private static Process StartSacrificialChild() => Process.Start(new ProcessStartInfo(
        "ping.exe",
        "-n 60 127.0.0.1")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true
    })!;

    private static ManagedProcessIdentity IdentityOf(Process process)
    {
        process.Refresh();
        return new ManagedProcessIdentity(
            process.Id,
            process.StartTime.ToUniversalTime(),
            Path.GetFullPath(process.MainModule!.FileName));
    }

    private static void Reap(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }

        process.Dispose();
    }

    [Fact]
    public void TerminateExactKillsOnlyAnExactIdentityMatch()
    {
        var child = StartSacrificialChild();
        try
        {
            var identity = IdentityOf(child);

            var result = new WindowsProcessInspector().TerminateExact(identity, TimeSpan.FromSeconds(10));

            Assert.Equal(ExactProcessTerminationState.ConfirmedGone, result.State);
            Assert.True(child.WaitForExit(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            Reap(child);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TerminateExactNeverKillsAReusedPidWithADifferentStartTimeOrPath(
        bool differentStart,
        bool differentPath)
    {
        var child = StartSacrificialChild();
        try
        {
            var live = IdentityOf(child);
            var expected = live with
            {
                ProcessStartTimeUtc = differentStart ? live.ProcessStartTimeUtc.AddSeconds(3) : live.ProcessStartTimeUtc,
                ExecutablePath = differentPath ? @"C:\Other\ETABS.exe" : live.ExecutablePath
            };

            var result = new WindowsProcessInspector().TerminateExact(expected, TimeSpan.FromSeconds(1));

            Assert.Equal(ExactProcessTerminationState.IdentityMismatchOrUnidentified, result.State);
            Assert.False(child.HasExited, "a mismatching identity must never be killed");
        }
        finally
        {
            Reap(child);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OpenExactRefusesAMismatchingIdentity(bool differentStart, bool differentPath)
    {
        var child = StartSacrificialChild();
        try
        {
            var live = IdentityOf(child);
            var expected = live with
            {
                ProcessStartTimeUtc = differentStart ? live.ProcessStartTimeUtc.AddSeconds(3) : live.ProcessStartTimeUtc,
                ExecutablePath = differentPath ? @"C:\Other\ETABS.exe" : live.ExecutablePath
            };

            using var handle = new WindowsProcessInspector().OpenExact(expected);

            Assert.Null(handle);
        }
        finally
        {
            Reap(child);
        }
    }
}
