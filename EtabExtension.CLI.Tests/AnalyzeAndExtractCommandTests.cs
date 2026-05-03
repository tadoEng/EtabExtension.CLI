using System.Text.Json;
using EtabExtension.CLI.Features.AnalyzeAndExtract;
using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using Xunit;

namespace EtabExtension.CLI.Tests;

public class AnalyzeAndExtractCommandTests
{
    [Fact]
    public void BuildFlatRequestSelectsAllDefaultTables()
    {
        var request = AnalyzeAndExtractCommand.BuildFlatRequest(
            filePath: "tower.edb",
            outputDir: "results",
            units: null,
            rawCases: null);

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
            filePath: "tower.edb",
            outputDir: "results",
            units: "SI_kN_m",
            rawCases: ["DEAD,LIVE", "EQX", "EQY, WINDX"]);

        Assert.Equal(["DEAD", "LIVE", "EQX", "EQY", "WINDX"], request.Cases);
    }

    [Fact]
    public void ModelMetadataSerializesCamelCaseShape()
    {
        var metadata = new ModelMetadata
        {
            FilePath = "tower.edb",
            EtabsVersion = "23.0.0",
            IsAnalyzed = true,
            IsLocked = true,
            Units = "kip/ft/F",
            LoadCases = [new LoadCaseInfo("DEAD", "LinStatic")],
            LoadCombinations = [new LoadComboInfo("ENV", "Envelope", ["DEAD"])],
            Stories = [new StoryInfo("L1", 14.0)],
            Groups = ["Core"],
            CollectedAt = DateTimeOffset.Parse("2026-05-03T00:00:00Z")
        };

        var json = JsonSerializer.Serialize(metadata, AnalyzeAndExtractService.MetadataJsonOptions);

        Assert.Contains("\"loadCases\"", json);
        Assert.Contains("\"loadCombinations\"", json);
        Assert.Contains("\"collectedAt\"", json);
    }
}
