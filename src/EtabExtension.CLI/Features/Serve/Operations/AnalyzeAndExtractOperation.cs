using System.Text.Json;
using EtabExtension.CLI.Features.AnalyzeAndExtract;
using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;
using EtabExtension.CLI.Features.GetStatus;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve.Operations;

public sealed class AnalyzeAndExtractOperation(
    IEtabsSession session,
    IAnalyzeAndExtractService analyze,
    IGetStatusService status,
    ICachedSessionStatus cachedStatus) : IOperationDefinition
{
    public string Kind => "analyze-and-extract";
    public TimeSpan OperationBudget => TimeSpan.FromMinutes(60);
    public TimeSpan StepBudget => TimeSpan.FromMinutes(15);

    public async Task<object> ExecuteAsync(JsonElement payload, OperationExecutionContext context)
    {
        var locator = Deserialize<ServeFileLocator>(payload);
        var request = Deserialize<AnalyzeAndExtractRequest>(payload);
        var app = session.GetOrStart();
        UpdateCachedStatus(app);
        try
        {
            return await analyze.AnalyzeAndExtractOnAppAsync(
                app,
                locator.FilePath,
                locator.OutputDir,
                request,
                context);
        }
        finally
        {
            UpdateCachedStatus(app);
        }
    }

    private void UpdateCachedStatus(EtabSharp.Core.ETABSApplication app)
    {
        try
        {
            cachedStatus.Update(status.GetStatusOnApp(app, session.ProcessId));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"⚠ Could not refresh cached daemon status: {ex.Message}");
        }
    }

    private static T Deserialize<T>(JsonElement payload) =>
        payload.Deserialize<T>(ServeJson.Options)
        ?? throw new InvalidOperationException("Operation payload deserialised to null");
}
