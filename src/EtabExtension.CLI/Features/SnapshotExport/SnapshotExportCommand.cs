using System.CommandLine;
using System.Text.Json;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Features.SnapshotExport.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.SnapshotExport;

public static class SnapshotExportCommand
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static Command Create(IServiceProvider services)
    {
        var command = new Command(
            "snapshot-export",
            "Export E2K, snapshot tables, and model metadata in one hidden ETABS session");

        var fileOption = new Option<string?>("--file")
        {
            Description = "Path to the .edb file",
            Required = false
        };
        fileOption.Aliases.Add("-f");

        var outputDirOption = new Option<string?>("--output-dir")
        {
            Description = "Directory for model.e2k, materials, and model-metadata.json",
            Required = false
        };
        outputDirOption.Aliases.Add("-o");

        var unitsOption = new Option<string?>("--units")
        {
            Description =
                $"Unit preset to normalise to before export. " +
                $"Default: {EtabsUnitPreset.Default}. " +
                $"Valid: {string.Join(", ", EtabsUnitPreset.All)}",
            Required = false
        };
        unitsOption.Aliases.Add("-u");

        var requestJsonOption = new Option<string?>("--request")
        {
            Description = "Full SnapshotExportRequest JSON. When provided, flat flags are ignored.",
            Required = false
        };
        requestJsonOption.Aliases.Add("-r");

        var profileOption = new Option<string?>("--profile")
        {
            Description = "Extraction profile for flat mode: snapshot, geometry, results, or full. Default: snapshot.",
            Required = false
        };

        command.Options.Add(fileOption);
        command.Options.Add(outputDirOption);
        command.Options.Add(unitsOption);
        command.Options.Add(requestJsonOption);
        command.Options.Add(profileOption);

        command.SetAction(async parseResult =>
        {
            var filePath = parseResult.GetValue(fileOption);
            var outputDir = parseResult.GetValue(outputDirOption);
            var requestJson = parseResult.GetValue(requestJsonOption);

            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(outputDir))
            {
                var fail = Result.Fail<SnapshotExportData>("--file and --output-dir are required");
                Environment.Exit(fail.ExitWithResult());
                return;
            }

            SnapshotExportRequest request;
            if (!string.IsNullOrWhiteSpace(requestJson))
            {
                try
                {
                    request = JsonSerializer.Deserialize<SnapshotExportRequest>(
                        requestJson,
                        RequestJsonOptions)
                        ?? throw new JsonException("--request JSON deserialised to null");
                }
                catch (JsonException ex)
                {
                    var fail = Result.Fail<SnapshotExportData>($"Invalid --request JSON: {ex.Message}");
                    Environment.Exit(fail.ExitWithResult());
                    return;
                }
            }
            else
            {
                request = BuildFlatRequest(
                    parseResult.GetValue(unitsOption),
                    parseResult.GetValue(profileOption));
            }

            var service = services.GetRequiredService<ISnapshotExportService>();
            var result = await service.SnapshotExportAsync(filePath!, outputDir!, request);
            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }

    internal static SnapshotExportRequest BuildFlatRequest(string? units, string? profile)
    {
        var normalisedProfile = string.IsNullOrWhiteSpace(profile)
            ? ExtractionProfiles.Snapshot
            : ExtractionProfiles.Normalise(profile);
        return new SnapshotExportRequest
        {
            Units = units,
            E2KFileName = "model.e2k",
            MaterialsDirName = "materials",
            MetadataFileName = "model-metadata.json",
            MetricsFileName = "run-metrics.json",
            ExtractionProfile = normalisedProfile,
            Tables = ExtractionProfiles.Build(normalisedProfile)
        };
    }

    internal static TableSelections BuildSnapshotTableSelections() =>
        ExtractionProfiles.Build(ExtractionProfiles.Snapshot);
}
