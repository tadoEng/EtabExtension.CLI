using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabExtension.CLI.Features.SaveModel;
using EtabExtension.CLI.Features.ReadModelMetadata;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Features.Serve.Inspection;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.Serve;

public static class ServeExtensions
{
    /// <summary>
    /// The daemon's record store. The trusted anchor (review P3-H) is passed THROUGH: a role
    /// record that lives outside <c>%LOCALAPPDATA%</c> (the B1 host's own record root) has no
    /// default anchor, and without this it would be checked from its root and refuse a
    /// legitimate redirected profile.
    /// </summary>
    public static JsonSessionRecordStore CreateRecordStore(ServeSessionPaths paths) =>
        new(paths.SessionRecordPath, lockWait: null, trustedAnchor: paths.TrustedAnchor);

    public static IServiceCollection AddServeFeature(this IServiceCollection services)
    {
        // Scoped: the serve command creates one scope for the daemon's lifetime,
        // so the shared ETABS session is a single instance for that run and the
        // dispatcher can depend on the scoped feature services.
        services.AddScoped<ServeSessionPaths>();
        services.AddScoped<LaunchPublicationCell>();
        services.AddScoped<ISessionRecordStore>(provider =>
            CreateRecordStore(provider.GetRequiredService<ServeSessionPaths>()));
        services.AddScoped<OrphanDispositionCell>();
        services.AddScoped<AbandonedRequests>();
        services.AddScoped<ShutdownIfUnchangedRegistry>();
        services.AddScoped<IEtabsChangeSignalSource, UnsupportedChangeSignalSource>();
        services.AddScoped<ISavedFileStampReader, DiskSavedFileStampReader>();
        services.AddScoped<IShutdownIfUnchangedApiFactory, SessionShutdownIfUnchangedApiFactory>();
        services.AddScoped<ShutdownIfUnchangedService>();
        services.AddScoped<IProcessInspector>(provider => new SiblingAwareProcessInspector(
            new WindowsProcessInspector(),
            provider.GetRequiredService<ServeSessionPaths>().SiblingRecordPath is { } sibling
                ? new JsonSiblingRecordSource(sibling)
                : new NoSiblingRecordSource(),
            provider.GetRequiredService<LaunchPublicationCell>()));
        services.AddScoped<IEtabsModalDialogWatchdog, EtabsModalDialogWatchdog>();
        services.AddScoped<IEtabsInstalledVersion>(provider => new SessionEtabsInstalledVersion(
            provider.GetRequiredService<IEtabsSession>(),
            new FileEtabsVersionProbe()));
        services.AddScoped<IManagedEtabsLauncher, ManagedEtabsLauncher>();
        services.AddScoped<IOrphanSessionCleaner, OrphanSessionCleaner>();
        services.AddScoped<IManagedEtabsStartIntentScope, ManagedEtabsStartIntentScope>();
        services.AddScoped<IEtabsWorkScope, EtabsWorkScope>();
        services.AddScoped<IEtabsWorkEnvelope, EtabsWorkEnvelope>();
        services.AddScoped<IEtabsSession, EtabsSession>();
        services.AddScoped<IStaExecutionWorker, StaExecutionWorker>();
        services.AddScoped<IServeShutdownCoordinator, ServeShutdownCoordinator>();
        services.AddScoped<IOperationEventJournalFactory, OperationEventJournalFactory>();
        services.AddScoped<IOperationClock, SystemOperationClock>();
        services.AddScoped<ICachedSessionStatus, CachedSessionStatus>();
        services.AddScoped<IOperationDefinition, AnalyzeAndExtractOperation>();
        services.AddScoped<IOperationManager, OperationManager>();
        services.AddScoped<IServeInspectionService, ServeInspectionService>();
        services.AddScoped<IEtabsInspectionApiFactory, EtabsInspectionApiFactory>();
        services.AddScoped<IServeDispatcher, ServeDispatcher>();
        services.AddScoped<IReadModelMetadataService, ReadModelMetadataService>();
        return services;
    }
}

/// <summary>
/// The daemon's record paths, set by <c>serve</c> from <c>--session-record</c> and
/// <c>--sibling-record</c> BEFORE any record store or census is resolved from the scope.
/// </summary>
public sealed class ServeSessionPaths
{
    /// <summary>Null keeps <see cref="JsonSessionRecordStore.DefaultPath"/>.</summary>
    public string? SessionRecordPath { get; set; }

    /// <summary>The other daemon's record; null when this daemon runs alone.</summary>
    public string? SiblingRecordPath { get; set; }

    /// <summary>
    /// The root the record directory is trusted under (<c>--record-anchor</c>, review P3-H). A
    /// reparse point ABOVE it is the user's own layout; one below it is refused. Null keeps the
    /// default (<c>%LOCALAPPDATA%</c>).
    /// </summary>
    public string? TrustedAnchor { get; set; }

    /// <summary>What a daemon does when stdin ends (<c>--on-eof</c>, card C2).</summary>
    public ServeEofPolicy EofPolicy { get; set; } = ServeEofPolicy.Exit;

    /// <summary>True for <c>serve --recover</c>: recovery only, no command may start ETABS (card v2.3 §4).</summary>
    public bool RecoveryOnly { get; set; }
}

/// <summary>Reads the document and change signal of the ETABS the session holds.</summary>
public sealed class SessionShutdownIfUnchangedApiFactory(IEtabsChangeSignalSource changeSignal)
    : IShutdownIfUnchangedApiFactory
{
    public IEtabsModelFileApi Create(IEtabsSession session) =>
        new EtabsModelFileApi(session.GetOrStart(), changeSignal);
}
