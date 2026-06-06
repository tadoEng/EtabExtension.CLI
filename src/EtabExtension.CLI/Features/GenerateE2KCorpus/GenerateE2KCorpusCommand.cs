using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using EtabExtension.CLI.Features.GenerateE2KCorpus.Models;
using EtabExtension.CLI.Shared.Common;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.GenerateE2KCorpus;

public static class GenerateE2KCorpusCommand
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public static Command Create(IServiceProvider services)
    {
        var command = new Command(
            "generate-e2k-corpus",
            "Create EDB/E2K parser fixtures through the ETABS API in one hidden session");

        var outputOption = new Option<string>("--output-dir")
        {
            Description = "Directory that receives one subdirectory per corpus case",
            Required = true
        };
        var requestOption = new Option<string?>("--request")
        {
            Description = "GenerateE2KCorpusRequest encoded as JSON"
        };
        var planVersionOption = new Option<int?>("--plan-version")
        {
            Description = "Generate the authoritative pairwise cases assigned to ETABS major version 22 or 23"
        };
        var etabsPathOption = new Option<string?>("--etabs-path")
        {
            Description = "Explicit ETABS.exe path; use this when multiple ETABS major versions are installed"
        };

        command.Options.Add(outputOption);
        command.Options.Add(requestOption);
        command.Options.Add(planVersionOption);
        command.Options.Add(etabsPathOption);

        command.SetAction(async parseResult =>
        {
            var outputDir = parseResult.GetValue(outputOption)!;
            var requestJson = parseResult.GetValue(requestOption);
            var planVersion = parseResult.GetValue(planVersionOption);
            var etabsPath = parseResult.GetValue(etabsPathOption);

            if ((requestJson is null) == (planVersion is null))
            {
                var failure = Result.Fail<GenerateE2KCorpusData>(
                    "Provide exactly one of --request or --plan-version.");
                Environment.Exit(failure.ExitWithResult());
                return;
            }

            GenerateE2KCorpusRequest? request;
            if (planVersion is not null)
            {
                try
                {
                    request = new GenerateE2KCorpusRequest
                    {
                        Cases = PairwiseCorpusPlanner.BuildDefaultCasesForVersion(
                            planVersion.Value),
                        ParseBudgetMs = 250,
                        EtabsProgramPath = etabsPath
                    };
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    var failure = Result.Fail<GenerateE2KCorpusData>(
                        exception.Message);
                    Environment.Exit(failure.ExitWithResult());
                    return;
                }
            }
            else
            {
                try
                {
                    request = JsonSerializer.Deserialize<GenerateE2KCorpusRequest>(
                        requestJson!,
                        RequestJsonOptions);
                }
                catch (JsonException exception)
                {
                    var failure = Result.Fail<GenerateE2KCorpusData>(
                        $"Invalid --request JSON: {exception.Message}");
                    Environment.Exit(failure.ExitWithResult());
                    return;
                }
            }

            if (request is null)
            {
                var failure = Result.Fail<GenerateE2KCorpusData>(
                    "Invalid --request JSON: payload was null.");
                Environment.Exit(failure.ExitWithResult());
                return;
            }

            if (!string.IsNullOrWhiteSpace(etabsPath))
            {
                request = request with { EtabsProgramPath = etabsPath };
            }

            var service = services.GetRequiredService<IGenerateE2KCorpusService>();
            var result = await service.GenerateAsync(outputDir, request);
            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }
}
