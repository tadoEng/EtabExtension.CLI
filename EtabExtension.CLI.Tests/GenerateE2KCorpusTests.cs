using EtabExtension.CLI.Features.GenerateE2KCorpus;
using EtabExtension.CLI.Features.GenerateE2KCorpus.Models;
using Xunit;

namespace EtabExtension.CLI.Tests;

public class GenerateE2KCorpusTests
{
    [Fact]
    public void DefaultPlannerBuildsDeterministicPairwiseCorpus()
    {
        var cases = PairwiseCorpusPlanner.BuildDefaultCases();

        Assert.Equal(36, cases.Count);
        Assert.Equal("case-001", cases[0].Id);
        Assert.Equal("case-036", cases[^1].Id);
        Assert.Empty(PairwiseCorpusPlanner.FindUncoveredPairs(cases));
        Assert.Equal(
            [
                "case-002",
                "case-003",
                "case-007",
                "case-008",
                "case-013",
                "case-015",
                "case-017",
                "case-019",
                "case-021",
                "case-023",
                "case-025",
                "case-027",
                "case-029",
                "case-031",
                "case-033",
                "case-035"
            ],
            cases
                .Where(item => item.ExpectedEtabsMajorVersion == 23)
                .Select(item => item.Id)
                .ToArray());
        Assert.Equal(20, cases.Count(item => item.ExpectedEtabsMajorVersion == 22));
    }

    [Fact]
    public void PlannerMapsFactorsToConcreteModelDimensions()
    {
        var planned = PairwiseCorpusPlanner.Plan(new CorpusCaseRequest
        {
            Id = "tower-metric",
            Shape = CorpusShape.Tower,
            Material = CorpusMaterial.Mixed,
            LateralSystem = CorpusLateralSystem.ShearWall,
            Grid = CorpusGrid.ThreeByThree,
            Units = CorpusUnits.Metric,
            ExpectedEtabsMajorVersion = 23
        });

        Assert.Equal(11, planned.Stories);
        Assert.Equal(3, planned.GridLinesX);
        Assert.Equal(3, planned.GridLinesY);
        Assert.Equal(3.6, planned.StoryHeight);
        Assert.Equal(8.0, planned.GridSpacing);
        Assert.Equal(23, planned.ExpectedEtabsMajorVersion);
    }

    [Fact]
    public void DefaultPlannerBuildsStablePerVersionSubsets()
    {
        var version22 = PairwiseCorpusPlanner.BuildDefaultCasesForVersion(22);
        var version23 = PairwiseCorpusPlanner.BuildDefaultCasesForVersion(23);

        Assert.Equal(20, version22.Count);
        Assert.Equal(16, version23.Count);
        Assert.All(
            version22,
            item => Assert.Equal(22, item.ExpectedEtabsMajorVersion));
        Assert.Equal(
            [
                "case-002",
                "case-003",
                "case-007",
                "case-008",
                "case-013",
                "case-015",
                "case-017",
                "case-019",
                "case-021",
                "case-023",
                "case-025",
                "case-027",
                "case-029",
                "case-031",
                "case-033",
                "case-035"
            ],
            version23.Select(item => item.Id).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PairwiseCorpusPlanner.BuildDefaultCasesForVersion(24));
    }

    [Fact]
    public void ValidatorRejectsUnsafeAndDuplicateCaseIds()
    {
        var request = new GenerateE2KCorpusRequest
        {
            Cases =
            [
                new CorpusCaseRequest
                {
                    Id = "../case-001",
                    ExpectedEtabsMajorVersion = 23
                },
                new CorpusCaseRequest
                {
                    Id = "../case-001",
                    ExpectedEtabsMajorVersion = 23
                }
            ]
        };

        var errors = CorpusRequestValidator.Validate(request);

        Assert.Contains(errors, error => error.Contains("safe file name", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unique", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsMissingExplicitEtabsExecutable()
    {
        var request = new GenerateE2KCorpusRequest
        {
            EtabsProgramPath = @"C:\missing\ETABS.exe",
            Cases =
            [
                new CorpusCaseRequest
                {
                    Id = "case-001",
                    ExpectedEtabsMajorVersion = 22
                }
            ]
        };

        var errors = CorpusRequestValidator.Validate(request);

        Assert.Contains(
            errors,
            error => error.Contains(
                "ETABS executable does not exist",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ManifestFormatterRecordsFactorsCountsAndBudget()
    {
        var planned = PairwiseCorpusPlanner.Plan(new CorpusCaseRequest
        {
            Id = "case-023",
            Shape = CorpusShape.MidRise,
            Material = CorpusMaterial.Mixed,
            LateralSystem = CorpusLateralSystem.BracedFrame,
            Grid = CorpusGrid.ThreeByThree,
            Units = CorpusUnits.Metric,
            ExpectedEtabsMajorVersion = 23
        });
        var counts = new CorpusModelCounts
        {
            Stories = 7,
            Materials = 5,
            Points = 63,
            Frames = 154,
            Areas = 28,
            LoadPatterns = 2
        };

        var manifest = CorpusManifestFormatter.Format(
            planned,
            counts,
            "23.2.0",
            250,
            "A1B2C3");

        Assert.Contains("case_id = \"case-023\"", manifest, StringComparison.Ordinal);
        Assert.Contains("source_kind = \"etabs-api-generated\"", manifest, StringComparison.Ordinal);
        Assert.Contains("sha256 = \"A1B2C3\"", manifest, StringComparison.Ordinal);
        Assert.Contains("lateral_system = \"braced_frame\"", manifest, StringComparison.Ordinal);
        Assert.Contains("etabs_version = \"23.2.0\"", manifest, StringComparison.Ordinal);
        Assert.Contains("n_frames = 154", manifest, StringComparison.Ordinal);
        Assert.Contains("parse_budget_ms = 250", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void OracleCountsMatchE2KCompressedDefinitions()
    {
        var planned = PairwiseCorpusPlanner.Plan(new CorpusCaseRequest
        {
            Id = "case-001",
            Shape = CorpusShape.Compact,
            Material = CorpusMaterial.Mixed,
            LateralSystem = CorpusLateralSystem.BracedFrame,
            Grid = CorpusGrid.ThreeByThree,
            Units = CorpusUnits.Metric,
            ExpectedEtabsMajorVersion = 23
        });

        var counts = CorpusOracleCounts.Calculate(
            planned,
            materialCount: 4,
            loadPatternCount: 2);

        Assert.Equal(5, counts.Stories);
        Assert.Equal(9, counts.Points);
        Assert.Equal(22, counts.Frames);
        Assert.Equal(4, counts.Areas);
    }
}
