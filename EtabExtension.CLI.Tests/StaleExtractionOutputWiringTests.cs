using System.Text;
using EtabExtension.CLI.Features.ExtractMaterials;
using EtabExtension.CLI.Features.ExtractMaterials.Models;
using EtabExtension.CLI.Features.ExtractResults;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Features.ExtractResults.Tables;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using EtabSharp.Core;
using EtabSharp.DatabaseTables.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// CLI #33, driven through the PRODUCTION entry points rather than the runner.
///
/// <para><see cref="StaleExtractionOutputTests"/> proves the runner's behaviour. These
/// prove that the code which ships actually reaches it. The helper every combined
/// extraction goes through could drop the runner, and the extract-materials
/// <c>--table-key</c> path writes its own fixed-name parquet without the runner at all.
/// Either would leave every runner-level test green while the shipped command kept
/// serving an earlier run's table.</para>
/// </summary>
public sealed class StaleExtractionOutputWiringTests : IDisposable
{
    private static readonly byte[] Sentinel = Encoding.ASCII.GetBytes("STALE-RUN-N-SENTINEL");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "etabextension-cli-tests", "stale-wiring", Guid.NewGuid().ToString("N"));

    private readonly string _outputDir;

    public StaleExtractionOutputWiringTests()
    {
        _outputDir = Path.Combine(_root, "out");
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not a test result.
        }
    }

    /// <summary>
    /// The shared helper that extract-results, snapshot-export and analyze-and-extract all
    /// call, with the real registry. No ETABS: the query factory ignores the application.
    /// </summary>
    [Fact]
    public async Task TheSharedExtractionHelperRemovesAPlannedTablesEarlierFile()
    {
        var stale = SeedStale(_outputDir, "base_reactions.parquet");
        var factory = new FakeTableFactory(new FakeQueryService(ZeroRowsFor: "Base Reactions"));

        var outcomes = await EtabsSessionHelpers.ExtractTablesOnOpenModelAsync(
            app: null!,
            new TableSelections { BaseReactions = TableFilter.All },
            _outputDir,
            isAnalyzed: true,
            isLocked: true,
            factory,
            RealRegistry(),
            new ParquetService());

        Assert.True(outcomes["base_reactions"].Success, outcomes["base_reactions"].Error);
        Assert.Equal(0, outcomes["base_reactions"].RowCount);
        Assert.False(File.Exists(stale), "the shipped helper left the previous run's table in place");
    }

    /// <summary>
    /// extract-materials with a non-default table key. No ETABS is supplied, so the command
    /// fails at its first ETABS call. The earlier file must already be gone by then: the
    /// path is cleared before any ETABS work, so no way of ending leaves it stale.
    /// </summary>
    [Fact]
    public async Task ExtractMaterialsWithATableKeyRemovesItsEarlierFileBeforeAnyEtabsWork()
    {
        var stale = SeedStale(_outputDir, "story_forces.parquet");
        var unrelated = SeedStale(_outputDir, "base_reactions.parquet");

        var result = await MaterialsService().ExtractMaterialsOnAppAsync(
            app: null!, Request(tableKey: "Story Forces"));

        Assert.False(result.Success);
        Assert.False(File.Exists(stale), "extract-materials left the previous run's table in place");
        Assert.True(File.Exists(unrelated), "extract-materials removed a table it did not extract");
    }

    /// <summary>
    /// The table key is caller input and the derived file is about to be DELETED, so a
    /// key that resolves outside the output directory must be refused before anything is
    /// touched.
    /// </summary>
    [Theory]
    [InlineData(@"..\outside")]
    [InlineData(@"C:outside")]
    [InlineData("..")]
    public async Task ATableKeyThatEscapesTheOutputDirectoryIsRefusedAndDeletesNothing(string tableKey)
    {
        var outside = SeedStale(_root, "outside.parquet");

        var result = await MaterialsService().ExtractMaterialsOnAppAsync(
            app: null!, Request(tableKey));

        Assert.False(result.Success);
        Assert.Contains("Nothing was written or removed", result.Error!, StringComparison.Ordinal);
        Assert.True(File.Exists(outside), "a file outside the output directory was removed");
    }

    /// <summary>
    /// A writer that puts bytes at the path and then REPORTS failure (rather than throwing)
    /// must not leave those bytes behind. With the earlier file already removed up front,
    /// only the extractor's own cleanup can make this pass.
    /// </summary>
    [Fact]
    public async Task AWriteThatReportsFailureAfterWritingLeavesNothingBehind()
    {
        var path = SeedStale(_outputDir, "base_reactions.parquet");
        var factory = new FakeTableFactory(new FakeQueryService(ZeroRowsFor: null));

        var outcomes = await EtabsSessionHelpers.ExtractTablesOnOpenModelAsync(
            app: null!,
            new TableSelections { BaseReactions = TableFilter.All },
            _outputDir,
            isAnalyzed: true,
            isLocked: true,
            factory,
            RealRegistry(),
            new WritesThenReportsFailure());

        Assert.False(outcomes["base_reactions"].Success);
        Assert.False(File.Exists(path), "a failed write left bytes at the table's path");
    }

    private ExtractMaterialsRequest Request(string tableKey)
    {
        var model = Path.Combine(_root, "model.edb");
        if (!File.Exists(model))
        {
            File.WriteAllBytes(model, [0]);
        }

        return new ExtractMaterialsRequest { FilePath = model, OutputDir = _outputDir, TableKey = tableKey };
    }

    private static ExtractMaterialsService MaterialsService() =>
        new(new ParquetService(), new FakeTableFactory(new FakeQueryService(null)), new UnusedExtractResults());

    private static string SeedStale(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, Sentinel);
        return path;
    }

    private static TableExtractorRegistry RealRegistry() => new(
        new MaterialListByStoryExtractor(NullLogger<MaterialListByStoryExtractor>.Instance),
        new MaterialPropertiesConcreteDataExtractor(NullLogger<MaterialPropertiesConcreteDataExtractor>.Instance),
        new GroupAssignmentsExtractor(NullLogger<GroupAssignmentsExtractor>.Instance),
        new StoryDefinitionsExtractor(NullLogger<StoryDefinitionsExtractor>.Instance),
        new BaseReactionsExtractor(NullLogger<BaseReactionsExtractor>.Instance),
        new StoryForcesExtractor(NullLogger<StoryForcesExtractor>.Instance),
        new JointDriftsExtractor(NullLogger<JointDriftsExtractor>.Instance),
        new PierForcesExtractor(NullLogger<PierForcesExtractor>.Instance),
        new PierSectionPropertiesExtractor(NullLogger<PierSectionPropertiesExtractor>.Instance),
        new ModalParticipatingMassRatios(NullLogger<ModalParticipatingMassRatios>.Instance));

    private sealed class FakeTableFactory(IEtabsTableQueryService query) : IEtabsTableServicesFactory
    {
        public IEtabsTableQueryService CreateQueryService(ETABSApplication app) => query;

        public IEtabsTableEditingService CreateEditingService(ETABSApplication app) =>
            throw new NotSupportedException();
    }

    /// <summary>Zero rows for the named table key; one row for every other table.</summary>
    private sealed class FakeQueryService(string? ZeroRowsFor) : IEtabsTableQueryService
    {
        public Task<TableQueryResult> QueryAsync(TableQueryRequest request) =>
            Task.FromResult(request.TableKey == ZeroRowsFor
                ? new TableQueryResult { IsSuccess = true, FieldKeys = ["Story", "Value"], RowCount = 0 }
                : new TableQueryResult
                {
                    IsSuccess = true,
                    FieldKeys = ["Story", "Value"],
                    Rows = [new Dictionary<string, string> { ["Story"] = "Base", ["Value"] = "1" }],
                    RowCount = 1,
                });

        public Task ClearLoadSelectionAsync() => Task.CompletedTask;

        public Task ResetSelectionAsync() => Task.CompletedTask;

        public Task<TableDataArrayResult> GetTableArrayAsync(
            string tableKey,
            string? groupName = null,
            string[]? fieldKeys = null) =>
            throw new NotSupportedException();
    }

    private sealed class WritesThenReportsFailure : IParquetService
    {
        public async Task<ParquetWriteResult> WriteAsync(
            string outputPath,
            List<string> fieldNames,
            List<string> flatData)
        {
            await File.WriteAllBytesAsync(outputPath, Encoding.ASCII.GetBytes("PAR1-half-written"));
            return new ParquetWriteResult(false, 0, outputPath, "row group rejected after the header was written");
        }
    }

    private sealed class UnusedExtractResults : IExtractResultsService
    {
        public Task<Result<ExtractResultsData>> ExtractAsync(ExtractResultsRequest request) =>
            throw new NotSupportedException("the non-default table-key path must not reach the combined extractor");

        public Task<Result<ExtractResultsData>> ExtractOnAppAsync(ETABSApplication app, ExtractResultsRequest request) =>
            throw new NotSupportedException("the non-default table-key path must not reach the combined extractor");
    }
}
