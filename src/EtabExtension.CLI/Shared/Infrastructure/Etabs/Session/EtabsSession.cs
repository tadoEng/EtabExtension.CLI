using EtabSharp.Core;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

/// <summary>
/// Owns the single, long-lived hidden ETABS instance for the <c>serve</c> daemon.
///
/// One instance per process lifetime: created lazily on first use, reused for
/// every command, and disposed exactly once on shutdown. This is what makes the
/// daemon a *single-ETABS-instance* sidecar — as opposed to the one-shot
/// commands, which each <c>CreateNew()</c>/<c>Connect()</c> their own instance
/// and so can leave multiple ETABS processes racing over COM.
/// </summary>
public interface IEtabsSession : IDisposable
{
    /// <summary>Returns the shared hidden ETABS app, starting it on first call.</summary>
    ETABSApplication GetOrStart();

    /// <summary>True once the shared instance has been started.</summary>
    bool IsStarted { get; }

    /// <summary>Exit + dispose the shared ETABS instance (idempotent).</summary>
    void Shutdown();
}

/// <inheritdoc />
public sealed class EtabsSession : IEtabsSession
{
    private readonly object _gate = new();
    private ETABSApplication? _app;

    public bool IsStarted
    {
        get { lock (_gate) { return _app is not null; } }
    }

    public ETABSApplication GetOrStart()
    {
        lock (_gate)
        {
            if (_app is not null)
            {
                return _app;
            }

            Console.Error.WriteLine("ℹ Starting ETABS (hidden, shared serve session)...");
            var app = ETABSWrapper.CreateNew()
                ?? throw new InvalidOperationException("Failed to start ETABS hidden instance.");
            app.Application.Hide();
            Console.Error.WriteLine($"✓ ETABS started hidden (v{app.FullVersion})");
            _app = app;
            return _app;
        }
    }

    public void Shutdown()
    {
        lock (_gate)
        {
            if (_app is null)
            {
                return;
            }

            try { _app.Application.ApplicationExit(false); }
            catch (Exception ex) { Console.Error.WriteLine($"⚠ ApplicationExit failed: {ex.Message}"); }

            try { _app.Dispose(); }
            catch (Exception ex) { Console.Error.WriteLine($"⚠ Dispose failed: {ex.Message}"); }

            _app = null;
            Console.Error.WriteLine("ℹ Shared ETABS session shut down.");
        }
    }

    public void Dispose() => Shutdown();
}
