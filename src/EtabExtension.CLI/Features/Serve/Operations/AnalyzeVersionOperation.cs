using System.Text.Json;
using EtabExtension.CLI.Features.AnalyzeVersion;
using EtabExtension.CLI.Features.AnalyzeVersion.Models;
using EtabExtension.CLI.Features.GetStatus;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve.Operations;

/// <summary>
/// <c>analyze-version</c>: a NEW operation kind next to <c>analyze-and-extract</c>, which stays
/// exactly as it is (card A6). It never starts ETABS, never opens a file and never saves.
/// </summary>
public sealed class AnalyzeVersionOperation(
    IEtabsSession session,
    IAnalyzeVersionService analyze,
    IAnalyzeVersionApiFactory apiFactory,
    IGetStatusService status,
    ICachedSessionStatus cachedStatus) : IOperationDefinition
{
    public string Kind => "analyze-version";
    public TimeSpan OperationBudget => TimeSpan.FromMinutes(60);
    public TimeSpan StepBudget => TimeSpan.FromMinutes(15);

    public async Task<object> ExecuteAsync(JsonElement payload, OperationExecutionContext context)
    {
        // Everything that can be refused without ETABS is refused first: zero COM calls and
        // zero session touches for a request with no units, policy or document.
        AnalyzeVersionRequest? request;
        try
        {
            request = payload.Deserialize<AnalyzeVersionRequest>(ServeJson.Options);
        }
        catch (JsonException exception)
        {
            return Result.Fail<AnalyzeVersionData>(
                $"Invalid analyze-version request: the payload could not be read ({exception.Message}).");
        }

        var valid = AnalyzeVersionRequestValidator.Validate(request, out var invalid);
        if (valid is null)
        {
            return Result.Fail<AnalyzeVersionData>(invalid!);
        }

        // A visible instance belongs to the user (card A1): refused here, before any COM call
        // and whatever the policy, so the daemon enforces it rather than trusting the host.
        if (session.IsUserVisible)
        {
            return Result.Fail<AnalyzeVersionData>(
                "Invalid analyze-version request: the ETABS instance is visible to the user; " +
                "analyze-version only runs against a hidden, daemon-owned instance.");
        }

        if (!File.Exists(valid.FilePath))
        {
            return Result.Fail<AnalyzeVersionData>($"File not found: {valid.FilePath}");
        }

        // The instance must already be running: this operation never cold-starts ETABS.
        if (!session.IsStarted)
        {
            return Result.Fail<AnalyzeVersionData>(
                $"[{AnalyzeVersionCodes.NotStarted}] No managed ETABS session is running; analyze-version never starts one.");
        }

        var app = session.GetOrStart();
        UpdateCachedStatus(app);
        try
        {
            return await analyze.AnalyzeOnApiAsync(apiFactory.Create(app), request!, context);
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
}
