using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Features.RunAnalysis.Models;
using EtabExtension.CLI.Features.ExtractResults.Tables;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Analysis;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metadata;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using EtabSharp.Core;
using ETABSv1;

namespace EtabExtension.CLI.Features.AnalyzeVersion;

/// <summary>
/// Every ETABS touch of <c>analyze-version</c>, as ONE seam so a fake can record each call
/// in order (analyze-version card C-1). The three units calls are the raw
/// <c>cSapModel</c> <c>_2</c> forms with their return codes and raw enum values (card M1),
/// never the unchecked EtabSharp wrapper.
/// </summary>
public interface IAnalyzeVersionApi
{
    /// <summary>The model ETABS currently holds (<c>GetModelFilename(true)</c>).</summary>
    string? GetModelFilename();

    /// <summary>Raw <c>cSapModel.GetDatabaseUnits_2</c>: the model's own units.</summary>
    int GetDatabaseUnits(out int force, out int length, out int temperature);

    /// <summary>Raw <c>cSapModel.GetPresentUnits_2</c>: the process-wide units for API data.</summary>
    int GetPresentUnits(out int force, out int length, out int temperature);

    /// <summary>Raw <c>cSapModel.SetPresentUnits_2</c>. Called only after the analysis has returned.</summary>
    int SetPresentUnits(int force, int length, int temperature);

    /// <summary>How many tables the resolved selection plans (pure; no ETABS call).</summary>
    int CountPlannedTables(TableSelections tables);

    /// <summary>The shared analysis runner (unchanged; it never saves).</summary>
    Task<Result<RunAnalysisData>> RunAnalysisAsync(string filePath, List<string>? cases, UnitInfo units);

    /// <summary>Whether any case finished and whether the model is locked.</summary>
    (bool IsAnalyzed, bool IsLocked) ReadModelState();

    Task<Dictionary<string, TableExtractionOutcome>> ExtractTablesAsync(
        TableSelections tables, string outputDir, bool isAnalyzed, bool isLocked);

    Task<ModelMetadata> CollectMetadataAsync(string filePath, UnitSnapshot units);

    /// <summary>
    /// <c>File.OpenFile</c>. The operation NEVER calls this (card I-2: the open document is
    /// verified, not re-opened); it is on the seam only so a test can prove it stays at zero.
    /// </summary>
    int OpenFile(string filePath);
}

public interface IAnalyzeVersionApiFactory
{
    IAnalyzeVersionApi Create(ETABSApplication application);
}

public sealed class AnalyzeVersionApiFactory(
    IEtabsTableServicesFactory tableFactory,
    TableExtractorRegistry registry,
    IParquetService parquet) : IAnalyzeVersionApiFactory
{
    public IAnalyzeVersionApi Create(ETABSApplication application) =>
        new EtabsAnalyzeVersionApi(application, tableFactory, registry, parquet);
}

internal sealed class EtabsAnalyzeVersionApi(
    ETABSApplication application,
    IEtabsTableServicesFactory tableFactory,
    TableExtractorRegistry registry,
    IParquetService parquet) : IAnalyzeVersionApi
{
    private cSapModel Model => application.SapModel;

    public string? GetModelFilename() => EtabsCurrentModelPath.Read(application);

    public int GetDatabaseUnits(out int force, out int length, out int temperature)
    {
        var rawForce = default(eForce);
        var rawLength = default(eLength);
        var rawTemperature = default(eTemperature);
        var code = Model.GetDatabaseUnits_2(ref rawForce, ref rawLength, ref rawTemperature);
        force = (int)rawForce;
        length = (int)rawLength;
        temperature = (int)rawTemperature;
        return code;
    }

    public int GetPresentUnits(out int force, out int length, out int temperature)
    {
        var rawForce = default(eForce);
        var rawLength = default(eLength);
        var rawTemperature = default(eTemperature);
        var code = Model.GetPresentUnits_2(ref rawForce, ref rawLength, ref rawTemperature);
        force = (int)rawForce;
        length = (int)rawLength;
        temperature = (int)rawTemperature;
        return code;
    }

    public int SetPresentUnits(int force, int length, int temperature) =>
        Model.SetPresentUnits_2((eForce)force, (eLength)length, (eTemperature)temperature);

    public int CountPlannedTables(TableSelections tables) =>
        registry.Entries.Count(entry => entry.FilterSelector(tables) is not null);

    public Task<Result<RunAnalysisData>> RunAnalysisAsync(string filePath, List<string>? cases, UnitInfo units) =>
        Task.FromResult(AnalysisRunner.Run(new EtabsAnalysisApi(application), filePath, cases, units));

    public (bool IsAnalyzed, bool IsLocked) ReadModelState() =>
        (application.Model.Analyze.GetCaseStatus().Any(status => status.IsFinished),
         application.Model.ModelInfo.IsLocked());

    public Task<Dictionary<string, TableExtractionOutcome>> ExtractTablesAsync(
        TableSelections tables, string outputDir, bool isAnalyzed, bool isLocked) =>
        EtabsSessionHelpers.ExtractTablesOnOpenModelAsync(
            application, tables, outputDir, isAnalyzed, isLocked, tableFactory, registry, parquet);

    public Task<ModelMetadata> CollectMetadataAsync(string filePath, UnitSnapshot units) =>
        EtabsSessionHelpers.CollectModelMetadataAsync(application, filePath, units);

    public int OpenFile(string filePath) =>
        throw new NotSupportedException("analyze-version never opens a file (card I-2).");
}
