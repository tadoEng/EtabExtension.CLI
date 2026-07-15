using System.CommandLine;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabExtension.CLI.Features.Serve.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.Serve;

public static class ServeCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command(
            "serve",
            "Long-lived daemon: one hidden ETABS instance shared across all requests. " +
            "Reads line-delimited JSON requests on stdin, writes one JSON response per line on stdout.");

        command.SetAction(async _ =>
        {
            // One DI scope for the daemon's whole life so the shared session and the
            // (scoped) feature services live and die together.
            using var scope = services.CreateScope();
            var provider = scope.ServiceProvider;
            var session = provider.GetRequiredService<IEtabsSession>();
            var dispatcher = provider.GetRequiredService<IServeDispatcher>();
            var operations = provider.GetRequiredService<IOperationManager>();
            var orphanCleaner = provider.GetRequiredService<IOrphanSessionCleaner>();

            // Program.cs redirects Console.Out to stderr — write the protocol to the
            // REAL stdout. "\n" line endings keep framing clean for the Rust reader.
            await using var stdout = new StreamWriter(Console.OpenStandardOutput())
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            using var stdin = new StreamReader(Console.OpenStandardInput());

            try
            {
                orphanCleaner.Clean();
                await new ServeLoop(dispatcher).RunAsync(stdin, stdout);
            }
            finally
            {
                operations.Dispose();
                session.Shutdown();
            }
        });

        return command;
    }
}
