using System.CommandLine;
using System.Text.Json;
using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.AnalyzeAndExtract;

public static class AnalyzeAndExtractCommand
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static Command Create(IServiceProvider services)
    {
        var command = new Command(
            "analyze-and-extract",
            "Run ETABS analysis and extract result tables in one hidden ETABS session");

        var fileOption = new Option<string?>("--file")
        {
            Description = "Path to the .edb file",
            Required = false
        };
        fileOption.Aliases.Add("-f");

        var outputDirOption = new Option<string?>("--output-dir")
        {
            Description = "Directory for parquet output and model-metadata.json",
            Required = false
        };
        outputDirOption.Aliases.Add("-o");

        var unitsOption = new Option<string?>("--units")
        {
            Description =
                $"Unit preset to normalise to before running analysis. " +
                $"Default: {EtabsUnitPreset.Default}. " +
                $"Valid: {string.Join(", ", EtabsUnitPreset.All)}",
            Required = false
        };
        unitsOption.Aliases.Add("-u");

        var casesOption = new Option<string[]?>("--cases")
        {
            Description = "Load case names to run. Supports space-separated tokens and comma-separated values.",
            Required = false,
            AllowMultipleArgumentsPerToken = true
        };
        casesOption.Aliases.Add("-c");

        var requestJsonOption = new Option<string?>("--request")
        {
            Description =
                "Full AnalyzeAndExtractRequest JSON. When provided, flat flags are ignored.",
            Required = false
        };
        requestJsonOption.Aliases.Add("-r");

        command.Options.Add(fileOption);
        command.Options.Add(outputDirOption);
        command.Options.Add(unitsOption);
        command.Options.Add(casesOption);
        command.Options.Add(requestJsonOption);

        command.SetAction(async parseResult =>
        {
            var filePath = parseResult.GetValue(fileOption);
            var outputDir = parseResult.GetValue(outputDirOption);
            var requestJson = parseResult.GetValue(requestJsonOption);

            AnalyzeAndExtractRequest request;
            if (!string.IsNullOrWhiteSpace(requestJson))
            {
                AnalyzeAndExtractRequest? parsedRequest;
                try
                {
                    parsedRequest = JsonSerializer.Deserialize<AnalyzeAndExtractRequest>(
                        requestJson,
                        RequestJsonOptions);
                }
                catch (JsonException ex)
                {
                    var fail = Result.Fail<AnalyzeAndExtractData>($"Invalid --request JSON: {ex.Message}");
                    Environment.Exit(fail.ExitWithResult());
                    return;
                }

                if (parsedRequest is null)
                {
                    var fail = Result.Fail<AnalyzeAndExtractData>("--request JSON deserialised to null");
                    Environment.Exit(fail.ExitWithResult());
                    return;
                }

                request = parsedRequest;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(outputDir))
                {
                    var fail = Result.Fail<AnalyzeAndExtractData>(
                        "--file and --output-dir are required when --request is not provided");
                    Environment.Exit(fail.ExitWithResult());
                    return;
                }

                request = BuildFlatRequest(
                    filePath!,
                    outputDir!,
                    parseResult.GetValue(unitsOption),
                    parseResult.GetValue(casesOption));
            }

            var (_, unitsError) = EtabsUnitPreset.Resolve(request.Units);
            if (unitsError is not null)
            {
                var fail = Result.Fail<AnalyzeAndExtractData>(unitsError);
                Environment.Exit(fail.ExitWithResult());
                return;
            }

            var service = services.GetRequiredService<IAnalyzeAndExtractService>();
            var result = await service.AnalyzeAndExtractAsync(request);
            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }

    internal static AnalyzeAndExtractRequest BuildFlatRequest(
        string filePath,
        string outputDir,
        string? units,
        string[]? rawCases)
    {
        return new AnalyzeAndExtractRequest
        {
            FilePath = filePath,
            OutputDir = outputDir,
            Units = units,
            Cases = SplitCases(rawCases),
            Tables = BuildDefaultTableSelections()
        };
    }

    private static List<string>? SplitCases(string[]? rawCases)
    {
        var cases = rawCases?
            .SelectMany(value => value.Split(
                [',', ' '],
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        return cases is { Count: > 0 } ? cases : null;
    }

    private static TableSelections BuildDefaultTableSelections() => new()
    {
        MaterialListByStory = new TableFilter(),
        MaterialPropertiesConcreteData = new TableFilter(),
        GroupAssignments = new TableFilter(),
        StoryDefinitions = new TableFilter(),
        PierSectionProperties = new TableFilter(),
        BaseReactions = TableFilter.All,
        StoryForces = TableFilter.All,
        JointDrifts = TableFilter.All,
        PierForces = TableFilter.All,
        ModalParticipatingMassRatios = new TableFilter()
    };
}
