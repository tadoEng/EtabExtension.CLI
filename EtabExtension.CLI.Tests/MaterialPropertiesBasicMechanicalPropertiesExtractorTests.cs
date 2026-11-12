using System.Text.Json;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Features.ExtractResults.Tables;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using EtabSharp.DatabaseTables.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// The Material + UnitWeight extractor: slug, table key, fixed two-column projection,
/// no analysis requirement, request key, and membership of the full, geometry and
/// snapshot profiles (but not the results profile).
/// </summary>
public sealed class MaterialPropertiesBasicMechanicalPropertiesExtractorTests : IDisposable
{
    private readonly string _outputDir = Path.Combine(
        Path.GetTempPath(), "etabextension-cli-tests", "basic-mech", Guid.NewGuid().ToString("N"));

    public MaterialPropertiesBasicMechanicalPropertiesExtractorTests() =>
        Directory.CreateDirectory(_outputDir);

    public void Dispose()
    {
        try { Directory.Delete(_outputDir, recursive: true); } catch (IOException) { }
    }

    private static MaterialPropertiesBasicMechanicalPropertiesExtractor NewExtractor() =>
        new(NullLogger<MaterialPropertiesBasicMechanicalPropertiesExtractor>.Instance);

    [Fact]
    public void IdentityAndAnalysisRequirement()
    {
        var extractor = NewExtractor();

        Assert.Equal("material_properties_basic_mechanical_properties", extractor.Slug);
        Assert.Equal("Material Properties - Basic Mechanical Properties", extractor.Label);
        Assert.False(extractor.RequiresAnalysis);
    }

    [Fact]
    public async Task QueriesOnlyMaterialAndUnitWeightWhateverTheFilterSays()
    {
        var query = new CapturingQuery();
        var filter = new TableFilter
        {
            FieldKeys = ["Material", "UnitMass", "E", "UnitWeight"],
            LoadCases = ["*"],
            LoadCombos = ["*"],
            Groups = ["*"],
        };

        var outcome = await NewExtractor().ExtractAsync(filter, _outputDir, query, new RecordingParquet());

        Assert.True(outcome.Success);
        var request = Assert.Single(query.Requests);
        Assert.Equal("Material Properties - Basic Mechanical Properties", request.TableKey);
        Assert.Equal(["Material", "UnitWeight"], request.FieldKeys);
        Assert.Null(request.LoadCases);
        Assert.Null(request.LoadCombos);
        Assert.Null(request.Groups);
    }

    [Fact]
    public void RequestKeyBindsToTheModelProperty()
    {
        var request = JsonSerializer.Deserialize<TableSelections>(
            """{"materialPropertiesBasicMechanicalProperties": {}}""");

        Assert.NotNull(request);
        Assert.NotNull(request!.MaterialPropertiesBasicMechanicalProperties);
        Assert.Null(request.MaterialPropertiesConcreteData);
    }

    [Fact]
    public void RegistryRoutesTheRequestKeyToTheExtractor()
    {
        var registry = RealRegistry();
        var selections = new TableSelections { MaterialPropertiesBasicMechanicalProperties = new TableFilter() };

        var selected = registry.Entries
            .Where(e => e.FilterSelector(selections) is not null)
            .Select(e => e.Extractor.Slug)
            .ToList();

        Assert.Equal(["material_properties_basic_mechanical_properties"], selected);
    }

    [Theory]
    [InlineData("full", true)]
    [InlineData("geometry", true)]
    [InlineData("snapshot", true)]
    [InlineData("results", false)]
    public void ProfilesIncludeTheTableOnlyWhereGeometryLives(string profile, bool included)
    {
        var tables = ExtractionProfiles.Build(profile);

        Assert.Equal(included, tables.MaterialPropertiesBasicMechanicalProperties is not null);
    }

    [Fact]
    public void ARequestForOnlyThisTableIsNotMistakenForEmpty()
    {
        var requested = new TableSelections { MaterialPropertiesBasicMechanicalProperties = new TableFilter() };

        var resolved = ExtractionProfiles.Resolve(requested, profile: "results", defaultProfile: "full");

        Assert.Same(requested, resolved);
    }

    private static TableExtractorRegistry RealRegistry() => new(
        new MaterialListByStoryExtractor(NullLogger<MaterialListByStoryExtractor>.Instance),
        new MaterialPropertiesConcreteDataExtractor(NullLogger<MaterialPropertiesConcreteDataExtractor>.Instance),
        NewExtractor(),
        new GroupAssignmentsExtractor(NullLogger<GroupAssignmentsExtractor>.Instance),
        new StoryDefinitionsExtractor(NullLogger<StoryDefinitionsExtractor>.Instance),
        new BaseReactionsExtractor(NullLogger<BaseReactionsExtractor>.Instance),
        new StoryForcesExtractor(NullLogger<StoryForcesExtractor>.Instance),
        new JointDriftsExtractor(NullLogger<JointDriftsExtractor>.Instance),
        new PierForcesExtractor(NullLogger<PierForcesExtractor>.Instance),
        new PierSectionPropertiesExtractor(NullLogger<PierSectionPropertiesExtractor>.Instance),
        new ModalParticipatingMassRatios(NullLogger<ModalParticipatingMassRatios>.Instance));

    private sealed class CapturingQuery : IEtabsTableQueryService
    {
        public List<TableQueryRequest> Requests { get; } = [];

        public Task<TableQueryResult> QueryAsync(TableQueryRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(new TableQueryResult
            {
                IsSuccess = true,
                FieldKeys = ["Material", "UnitWeight"],
                Rows = [new Dictionary<string, string> { ["Material"] = "C6000", ["UnitWeight"] = "0.15" }],
                RowCount = 1,
            });
        }

        public Task ClearLoadSelectionAsync() => Task.CompletedTask;

        public Task ResetSelectionAsync() => Task.CompletedTask;

        public Task<TableDataArrayResult> GetTableArrayAsync(
            string tableKey,
            string? groupName = null,
            string[]? fieldKeys = null) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingParquet : IParquetService
    {
        public Task<ParquetWriteResult> WriteAsync(
            string outputPath,
            List<string> fieldNames,
            List<string> flatData) =>
            Task.FromResult(new ParquetWriteResult(true, 1, outputPath, null));
    }
}
