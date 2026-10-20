using System.Text;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Features.ExtractResults.Tables;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using EtabSharp.DatabaseTables.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Parquet;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// CLI #33: an extraction attempt must never leave an earlier run's bytes at a table's
/// output path while that path reads as this run's result.
///
/// <para>Every test pre-seeds the table's real output path with sentinel bytes that are
/// not even parquet. The assertion is then simply whether those bytes survive. The
/// discriminating case is the one where the CURRENT run writes nothing — zero rows, a
/// failed query, a failed write, a skipped table — because that is exactly when the old
/// file used to stay behind looking current.</para>
/// </summary>
public sealed class StaleExtractionOutputTests : IDisposable
{
    private static readonly byte[] Sentinel = Encoding.ASCII.GetBytes("STALE-RUN-N-SENTINEL");

    private readonly string _outputDir = Path.Combine(
        Path.GetTempPath(), "etabextension-cli-tests", "stale-output", Guid.NewGuid().ToString("N"));

    public StaleExtractionOutputTests()
    {
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_outputDir, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not a test result.
        }
    }

    [Fact]
    public async Task AZeroRowTableLeavesNoEarlierFileBehind()
    {
        var path = SeedStale("base_reactions");
        var query = new FakeQueryService().Returns("Base Reactions", ZeroRows());

        var outcomes = await RunAsync([Table("base_reactions", "Base Reactions")], query);

        var outcome = outcomes["base_reactions"];
        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal(0, outcome.RowCount);
        Assert.Null(outcome.OutputFile);
        Assert.False(File.Exists(path), "the previous run's file survived a zero-row run");
    }

    [Fact]
    public async Task AFailedQueryLeavesNoEarlierFileBehindAndFailsExplicitly()
    {
        var path = SeedStale("story_forces");
        var query = new FakeQueryService().Returns(
            "Story Forces", TableQueryResult.Fail("Story Forces", "DatabaseTables.GetTableForDisplayArray failed (ret=1)"));

        var outcomes = await RunAsync([Table("story_forces", "Story Forces")], query);

        var outcome = outcomes["story_forces"];
        Assert.False(outcome.Success);
        Assert.Contains("ret=1", outcome.Error!, StringComparison.Ordinal);
        Assert.False(File.Exists(path), "the previous run's file survived a failed query");
    }

    [Fact]
    public async Task AFailedWriteLeavesNoEarlierFileBehindAndFailsExplicitly()
    {
        var path = SeedStale("joint_drifts");
        var query = new FakeQueryService().Returns("Joint Drifts", Rows(("L1", "0.002")));

        var outcomes = await RunAsync(
            [Table("joint_drifts", "Joint Drifts")], query, new ScriptedParquet(WriteMode.ReturnFailure));

        var outcome = outcomes["joint_drifts"];
        Assert.False(outcome.Success);
        Assert.Contains("Parquet write failed", outcome.Error!, StringComparison.Ordinal);
        Assert.False(File.Exists(path), "the previous run's file survived a failed write");
    }

    /// <summary>
    /// The writer streams straight into the final path, so a write that dies part-way
    /// leaves THIS run's truncated bytes there. Removing the old file first is not enough
    /// on its own; the extractor must also clear what its own failed write left.
    /// </summary>
    [Fact]
    public async Task AnInterruptedWriteLeavesNeitherTheEarlierFileNorAPartialOne()
    {
        var path = SeedStale("pier_forces");
        var query = new FakeQueryService().Returns("Pier Forces", Rows(("P1", "120")));

        var outcomes = await RunAsync(
            [Table("pier_forces", "Pier Forces")], query, new ScriptedParquet(WriteMode.ThrowAfterPartialWrite));

        var outcome = outcomes["pier_forces"];
        Assert.False(outcome.Success);
        Assert.Contains("disk vanished", outcome.Error!, StringComparison.Ordinal);
        Assert.False(File.Exists(path), "a stale or partially written file is still at the table's path");
    }

    [Fact]
    public async Task ASuccessfulTablePublishesOnlyThisRunsBytes()
    {
        var path = SeedStale("base_reactions");
        var query = new FakeQueryService().Returns("Base Reactions", Rows(("Base", "42.5")));

        var outcomes = await RunAsync([Table("base_reactions", "Base Reactions")], query);

        var outcome = outcomes["base_reactions"];
        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal(path, outcome.OutputFile);
        Assert.NotEqual(Sentinel, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(["Base", "42.5"], await ReadSingleRowAsync(path));
    }

    [Fact]
    public async Task AResultsTableSkippedOnAnUnanalyzedModelLeavesNoEarlierFileBehind()
    {
        var path = SeedStale("modal_participating_mass_ratios");
        var query = new FakeQueryService();

        var outcomes = await RunAsync(
            [Table("modal_participating_mass_ratios", "Modal Participating Mass Ratios")],
            query,
            isAnalyzed: false);

        var outcome = outcomes["modal_participating_mass_ratios"];
        Assert.False(outcome.Success);
        Assert.Contains("no analysis results", outcome.Error!, StringComparison.Ordinal);
        Assert.Empty(query.Queried);
        Assert.False(File.Exists(path), "a skipped table's previous file survived");
    }

    [Fact]
    public async Task OneFailedOrEmptyTableCannotKeepStaleBytesWhileOthersSucceed()
    {
        var fresh = SeedStale("base_reactions");
        var empty = SeedStale("story_forces");
        var failed = SeedStale("joint_drifts");
        var skipped = SeedStale("pier_forces");
        var query = new FakeQueryService()
            .Returns("Base Reactions", Rows(("Base", "7")))
            .Returns("Story Forces", ZeroRows())
            .Returns("Joint Drifts", TableQueryResult.Fail("Joint Drifts", "query failed"));

        var outcomes = await RunAsync(
            [
                Table("base_reactions", "Base Reactions", requiresAnalysis: false),
                Table("story_forces", "Story Forces", requiresAnalysis: false),
                Table("joint_drifts", "Joint Drifts", requiresAnalysis: false),
                Table("pier_forces", "Pier Forces", requiresAnalysis: true),
            ],
            query,
            isAnalyzed: false);

        Assert.True(outcomes["base_reactions"].Success);
        Assert.Equal(["Base", "7"], await ReadSingleRowAsync(fresh));
        Assert.True(outcomes["story_forces"].Success);
        Assert.False(outcomes["joint_drifts"].Success);
        Assert.False(outcomes["pier_forces"].Success);
        Assert.False(File.Exists(empty), "the empty table kept its previous file");
        Assert.False(File.Exists(failed), "the failed table kept its previous file");
        Assert.False(File.Exists(skipped), "the skipped table kept its previous file");
    }

    /// <summary>
    /// The invalidation is exact-path only. Nothing the request did not plan is touched:
    /// a table it did not ask for, a same-stem file of another type, a caller's own file,
    /// and a subdirectory holding a same-named parquet all survive.
    /// </summary>
    [Fact]
    public async Task OnlyThePlannedTablesExactPathsAreRemoved()
    {
        SeedStale("base_reactions");
        var unplanned = SeedStale("story_forces");
        var sameStem = Seed("base_reactions.csv");
        var callerFile = Seed("notes.txt");
        var nested = Seed(Path.Combine("archive", "base_reactions.parquet"));
        var query = new FakeQueryService().Returns("Base Reactions", ZeroRows());

        await RunAsync(
            [
                Table("base_reactions", "Base Reactions"),
                Table("story_forces", "Story Forces", planned: false),
            ],
            query);

        Assert.True(File.Exists(unplanned), "an unplanned table's file was removed");
        Assert.True(File.Exists(sameStem), "a non-parquet file with the same stem was removed");
        Assert.True(File.Exists(callerFile), "a caller's own file was removed");
        Assert.True(File.Exists(nested), "a file in a subdirectory was removed");
        Assert.Equal(["Base Reactions"], query.Queried);
    }

    /// <summary>
    /// When the earlier file cannot be removed, the table must not be attempted and must
    /// not report success: a write failing for the same reason would otherwise leave the
    /// old bytes under a fresh outcome.
    /// </summary>
    [Fact]
    public async Task AnEarlierFileThatCannotBeRemovedFailsTheTableWithoutQueryingIt()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "Only Windows refuses to delete a file another handle holds with FileShare.None.");

        var path = SeedStale("base_reactions");
        var query = new FakeQueryService().Returns("Base Reactions", Rows(("Base", "1")));

        Dictionary<string, TableExtractionOutcome> outcomes;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            outcomes = await RunAsync([Table("base_reactions", "Base Reactions")], query);
        }

        var outcome = outcomes["base_reactions"];
        Assert.False(outcome.Success);
        Assert.Contains("could not be removed", outcome.Error!, StringComparison.Ordinal);
        Assert.Contains("must NOT be read", outcome.Error!, StringComparison.Ordinal);
        Assert.Empty(query.Queried);
    }

    /// <summary>
    /// Invalidation happens for EVERY planned table before the first attempt, so an
    /// extraction that dies after its first table cannot leave the later tables' old
    /// files in place.
    /// </summary>
    [Fact]
    public async Task AnAttemptThatDiesPartWayLeavesLaterTablesAbsentNotStale()
    {
        var later = SeedStale("story_forces");
        var query = new FakeQueryService().Returns("Story Forces", Rows(("L1", "3")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
            [
                new TableRegistration(_ => TableFilter.All, new ThrowingExtractor("base_reactions")),
                Table("story_forces", "Story Forces"),
            ],
            query));

        Assert.False(File.Exists(later), "a table after the failure point kept its previous file");
    }

    private async Task<Dictionary<string, TableExtractionOutcome>> RunAsync(
        IReadOnlyList<TableRegistration> entries,
        FakeQueryService query,
        IParquetService? parquet = null,
        bool isAnalyzed = true) =>
        await TableExtractionRunner.RunAsync(
            entries,
            new TableSelections(),
            _outputDir,
            isAnalyzed,
            query,
            parquet ?? new ParquetService());

    private static TableRegistration Table(
        string slug,
        string key,
        bool requiresAnalysis = true,
        bool planned = true) =>
        new(_ => planned ? TableFilter.All : null, new TestExtractor(slug, key, requiresAnalysis));

    private string SeedStale(string slug) => Seed($"{slug}.parquet");

    private string Seed(string relativePath)
    {
        var path = Path.Combine(_outputDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Sentinel);
        return path;
    }

    private static TableQueryResult ZeroRows() => new()
    {
        IsSuccess = true,
        FieldKeys = ["Story", "Value"],
        RowCount = 0,
    };

    private static TableQueryResult Rows(params (string Story, string Value)[] rows) => new()
    {
        IsSuccess = true,
        FieldKeys = ["Story", "Value"],
        Rows = rows
            .Select(row => new Dictionary<string, string> { ["Story"] = row.Story, ["Value"] = row.Value })
            .ToList(),
        RowCount = rows.Length,
    };

    private static async Task<string[]> ReadSingleRowAsync(string path)
    {
        using var reader = await ParquetReader.CreateAsync(
            path, cancellationToken: TestContext.Current.CancellationToken);
        using var group = reader.OpenRowGroupReader(0);
        var values = new List<string>();
        foreach (var field in reader.Schema.GetDataFields())
        {
            var column = await group.ReadColumnAsync(field, TestContext.Current.CancellationToken);
            values.Add((string)column.Data.GetValue(0)!);
        }

        return [.. values];
    }

    private sealed class TestExtractor(string slug, string key, bool requiresAnalysis)
        : TableExtractorBase(NullLogger.Instance)
    {
        public override string Slug => slug;

        public override string Label => key;

        public override bool RequiresAnalysis => requiresAnalysis;

        protected override string EtabsTableKey => key;
    }

    private sealed class ThrowingExtractor(string slug) : ITableExtractor
    {
        public string Slug => slug;

        public string Label => slug;

        public bool RequiresAnalysis => false;

        public Task<TableExtractionOutcome> ExtractAsync(
            TableFilter filter,
            string outputDir,
            IEtabsTableQueryService queryService,
            IParquetService parquet) =>
            throw new InvalidOperationException("the session died mid-extraction");
    }

    private sealed class FakeQueryService : IEtabsTableQueryService
    {
        private readonly Dictionary<string, TableQueryResult> _results = new(StringComparer.Ordinal);

        public List<string> Queried { get; } = [];

        public FakeQueryService Returns(string tableKey, TableQueryResult result)
        {
            _results[tableKey] = result;
            return this;
        }

        public Task<TableQueryResult> QueryAsync(TableQueryRequest request)
        {
            Queried.Add(request.TableKey);
            return Task.FromResult(_results.TryGetValue(request.TableKey, out var result)
                ? result
                : TableQueryResult.Fail(request.TableKey, "no scripted result"));
        }

        public Task ClearLoadSelectionAsync() => Task.CompletedTask;

        public Task ResetSelectionAsync() => Task.CompletedTask;

        public Task<TableDataArrayResult> GetTableArrayAsync(
            string tableKey,
            string? groupName = null,
            string[]? fieldKeys = null) =>
            throw new NotSupportedException();
    }

    private enum WriteMode
    {
        ReturnFailure,
        ThrowAfterPartialWrite,
    }

    private sealed class ScriptedParquet(WriteMode mode) : IParquetService
    {
        public async Task<ParquetWriteResult> WriteAsync(
            string outputPath,
            List<string> fieldNames,
            List<string> flatData)
        {
            if (mode == WriteMode.ReturnFailure)
            {
                return new ParquetWriteResult(false, 0, outputPath, "row group rejected");
            }

            // What the real writer leaves when it dies after File.Create: this run's own
            // truncated bytes, at the final path.
            await File.WriteAllBytesAsync(outputPath, Encoding.ASCII.GetBytes("PAR1-truncated"));
            throw new IOException("disk vanished mid-write");
        }
    }
}
