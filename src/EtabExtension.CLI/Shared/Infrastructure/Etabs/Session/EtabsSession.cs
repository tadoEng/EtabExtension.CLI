using EtabSharp.Core;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

public interface IEtabsSession : IDisposable
{
    ETABSApplication GetOrStart();
    IManagedEtabsApplication GetOrStartOwned();
    bool IsStarted { get; }
    int? ProcessId { get; }
    void Shutdown();
}

public sealed class EtabsSession(
    IManagedEtabsLauncher launcher,
    IProcessInspector processes,
    ISessionRecordStore records) : IEtabsSession
{
    private readonly object _gate = new();
    private IManagedEtabsApplication? _owned;

    public bool IsStarted { get { lock (_gate) return _owned is not null; } }
    public int? ProcessId { get { lock (_gate) return _owned?.Identity.Pid; } }

    public ETABSApplication GetOrStart() => GetOrStartOwned().Application;

    public IManagedEtabsApplication GetOrStartOwned()
    {
        lock (_gate)
        {
            if (_owned is null)
            {
                Console.Error.WriteLine("ℹ Starting ETABS (hidden, shared serve session)...");
                var launched = launcher.Launch();
                try
                {
                    records.Write(ToRecord(launched));
                    _owned = launched;
                }
                catch
                {
                    try { launched.ExitWithoutSaving(); } catch { }
                    try { launched.Dispose(); } catch { }
                    throw;
                }
                Console.Error.WriteLine($"✓ ETABS started hidden (PID {_owned.Identity.Pid})");
            }

            try
            {
                Verify(_owned);
                return _owned;
            }
            catch
            {
                try { _owned.ExitWithoutSaving(); } catch { }
                try { _owned.Dispose(); } catch { }
                _owned = null;
                records.Clear();
                throw;
            }
        }
    }

    private void Verify(IManagedEtabsApplication owned)
    {
        var record = records.Read();
        var live = processes.Find(owned.Identity.Pid);
        if (record is null || live is null
            || record.ManagedLaunchRecordId != owned.ManagedLaunchRecordId
            || !OrphanSessionCleaner.IdentityMatches(record, live)
            || live != owned.Identity)
        {
            throw new InvalidOperationException(
                "Managed ETABS identity verification failed; a clean reopen is required.");
        }
    }

    public void Shutdown()
    {
        lock (_gate)
        {
            if (_owned is null) { records.Clear(); return; }
            try { _owned.ExitWithoutSaving(); }
            catch (Exception ex) { Console.Error.WriteLine($"⚠ ApplicationExit failed: {ex.Message}"); }
            try { _owned.Dispose(); }
            catch (Exception ex) { Console.Error.WriteLine($"⚠ Dispose failed: {ex.Message}"); }
            _owned = null;
            records.Clear();
            Console.Error.WriteLine("ℹ Shared ETABS session shut down.");
        }
    }

    public void Dispose() => Shutdown();

    private static ManagedEtabsSessionRecord ToRecord(IManagedEtabsApplication owned) => new(
        1,
        owned.Identity.Pid,
        owned.Identity.ProcessStartTimeUtc,
        Path.GetFullPath(owned.Identity.ExecutablePath),
        owned.ManagedLaunchRecordId,
        DateTimeOffset.UtcNow);
}
