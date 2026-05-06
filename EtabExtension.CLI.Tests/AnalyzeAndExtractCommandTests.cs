using System.Text.Json;
using EtabExtension.CLI.Features.AnalyzeAndExtract;
using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;
using EtabExtension.CLI.Features.SnapshotExport;
using EtabExtension.CLI.Features.SnapshotExport.Models;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metadata;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metrics;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using Xunit;

namespace EtabExtension.CLI.Tests;

public class AnalyzeAndExtractCommandTests
{
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void BuildFlatRequestSelectsAllDefaultTables()
    {
        var request = AnalyzeAndExtractCommand.BuildFlatRequest(
            units: null,
            rawCases: null,
            profile: null);

        var baseReactions = Assert.IsType<Features.ExtractResults.Models.TableFilter>(
            request.Tables.BaseReactions);
        var baseLoadCases = baseReactions.LoadCases
            ?? throw new InvalidOperationException("Base reactions should select all cases.");
        var baseLoadCombos = baseReactions.LoadCombos
            ?? throw new InvalidOperationException("Base reactions should select all combos.");
        Assert.Equal([TableQueryRequest.Wildcard], baseLoadCases);
        Assert.Equal([TableQueryRequest.Wildcard], baseLoadCombos);

        var materialListByStory = Assert.IsType<Features.ExtractResults.Models.TableFilter>(
            request.Tables.MaterialListByStory);
        Assert.Null(materialListByStory.LoadCases);
        Assert.Null(materialListByStory.LoadCombos);
    }

    [Fact]
    public void BuildFlatRequestSplitsCommaAndSpaceSeparatedCases()
    {
        var request = AnalyzeAndExtractCommand.BuildFlatRequest(
            units: "SI_kN_m",
            rawCases: ["DEAD,LIVE", "EQX", "EQY, WINDX"],
            profile: null);

        Assert.Equal(["DEAD", "LIVE", "EQX", "EQY", "WINDX"], request.Cases);
    }

    [Fact]
    public void BuildFlatRequestCanSelectGeometryProfile()
    {
        var request = AnalyzeAndExtractCommand.BuildFlatRequest(
            units: null,
            rawCases: null,
            profile: "geometry");

        Assert.NotNull(request.Tables.StoryDefinitions);
        Assert.NotNull(request.Tables.PierSectionProperties);
        Assert.NotNull(request.Tables.GroupAssignments);
        Assert.NotNull(request.Tables.MaterialListByStory);
        Assert.Null(request.Tables.BaseReactions);
        Assert.Null(request.Tables.StoryForces);
        Assert.Equal("geometry", request.ExtractionProfile);
    }

    [Fact]
    public void ModelMetadataSerializesCamelCaseShape()
    {
        var metadata = new ModelMetadata
        {
            SchemaVersion = 2,
            FilePath = "tower.edb",
            EtabsVersion = "23.0.0",
            IsAnalyzed = true,
            IsLocked = true,
            Units = "kip/ft/F",
            LoadPatterns = [new LoadPatternInfo("DEAD", "Dead", 1.0)],
            LoadCases = [new LoadCaseInfo("DEAD", "LinStatic")],
            LoadCombinations =
            [
                new LoadComboInfo(
                    "ENV",
                    "Envelope",
                    ["DEAD"],
                    [new LoadComboItemInfo("DEAD", "LoadCase", 1.0, 0)])
            ],
            Stories = [new StoryInfo("L1", 14.0, 14.0, true, null)],
            Groups = ["Core"],
            GroupDetails = [new GroupInfo("Core", 42)],
            Materials =
            [
                new MaterialInfo
                {
                    Name = "C5000",
                    MaterialType = "Concrete",
                    SymType = "Isotropic",
                    WeightPerVolume = 0.15,
                    ConcreteFc = 5.0
                }
            ],
            FrameSections = [new FrameSectionInfo("C24x24", "ConcreteRectangular")],
            AreaSections = [new AreaSectionInfo("WALL-12", "Wall")],
            Warnings = [new MetadataWarning("groups", "partial group warning")],
            CollectedAt = DateTimeOffset.Parse("2026-05-03T00:00:00Z")
        };

        var json = JsonSerializer.Serialize(metadata, AnalyzeAndExtractService.MetadataJsonOptions);

        Assert.Contains("\"schemaVersion\"", json);
        Assert.Contains("\"loadPatterns\"", json);
        Assert.Contains("\"loadCases\"", json);
        Assert.Contains("\"loadCombinations\"", json);
        Assert.Contains("\"items\"", json);
        Assert.Contains("\"groupDetails\"", json);
        Assert.Contains("\"materials\"", json);
        Assert.Contains("\"frameSections\"", json);
        Assert.Contains("\"areaSections\"", json);
        Assert.Contains("\"warnings\"", json);
        Assert.Contains("\"collectedAt\"", json);
    }

    [Fact]
    public void AnalyzeAndExtractRequestAcceptsMetadataOutputPath()
    {
        var request = JsonSerializer.Deserialize<AnalyzeAndExtractRequest>(
            """
            {
              "units": "US_Kip_Ft",
              "metadataOutputPath": "C:\\Models\\v1\\model-metadata.json",
              "metricsOutputPath": "C:\\Models\\v1\\run-metrics.json",
              "extractionProfile": "results",
              "tables": {}
            }
            """,
            CaseInsensitiveJson);

        Assert.Equal(@"C:\Models\v1\model-metadata.json", request?.MetadataOutputPath);
        Assert.Equal(@"C:\Models\v1\run-metrics.json", request?.MetricsOutputPath);
        Assert.Equal("results", request?.ExtractionProfile);
    }

    [Fact]
    public void SnapshotExportFlatRequestUsesDefaultPathsAndSnapshotTables()
    {
        var request = SnapshotExportCommand.BuildFlatRequest(units: "US_Kip_Ft", profile: null);

        Assert.Equal("US_Kip_Ft", request.Units);
        Assert.Equal("model.e2k", request.E2KFileName);
        Assert.Equal("materials", request.MaterialsDirName);
        Assert.Equal("model-metadata.json", request.MetadataFileName);
        Assert.Equal("run-metrics.json", request.MetricsFileName);
        Assert.Equal("snapshot", request.ExtractionProfile);
        Assert.NotNull(request.Tables.MaterialListByStory);
        Assert.NotNull(request.Tables.MaterialPropertiesConcreteData);
        Assert.NotNull(request.Tables.GroupAssignments);
        Assert.NotNull(request.Tables.StoryDefinitions);
        Assert.NotNull(request.Tables.PierSectionProperties);
        Assert.Null(request.Tables.BaseReactions);
    }

    [Fact]
    public void RunMetricsSerializesCamelCaseShape()
    {
        var metrics = new RunMetrics
        {
            SchemaVersion = 1,
            Command = "snapshot-export",
            FilePath = "tower.edb",
            OutputDir = "v1",
            TotalElapsedMs = 500,
            Phases =
            [
                new RunPhaseMetric("openModel", 120, true, null),
                new RunPhaseMetric("metadata.materials", 30, false, "partial warning")
            ],
            CollectedAt = DateTimeOffset.Parse("2026-05-07T00:00:00Z")
        };

        var json = JsonSerializer.Serialize(metrics, AnalyzeAndExtractService.MetadataJsonOptions);

        Assert.Contains("\"schemaVersion\"", json);
        Assert.Contains("\"command\"", json);
        Assert.Contains("\"phases\"", json);
        Assert.Contains("\"elapsedMs\"", json);
        Assert.Contains("\"openModel\"", json);
    }
}
