using System.Diagnostics;
using System.Security.Cryptography;
using EtabExtension.CLI.Features.GenerateE2KCorpus.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabSharp.Core;
using EtabSharp.Elements.AreaObj.Models;
using EtabSharp.Properties.Areas.Models;
using ETABSv1;

namespace EtabExtension.CLI.Features.GenerateE2KCorpus;

public sealed class GenerateE2KCorpusService : IGenerateE2KCorpusService
{
    private const string ColumnSection = "CORPUS-COLUMN";
    private const string BeamSection = "CORPUS-BEAM";
    private const string BraceSection = "CORPUS-BRACE";
    private const string SlabPropertyName = "CORPUS-SLAB";
    private const string WallPropertyName = "CORPUS-WALL";

    public async Task<Result<GenerateE2KCorpusData>> GenerateAsync(
        string outputDir,
        GenerateE2KCorpusRequest request)
    {
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return Result.Fail<GenerateE2KCorpusData>("OutputDir cannot be empty.");
        }

        var validationErrors = CorpusRequestValidator.Validate(request);
        if (validationErrors.Count > 0)
        {
            return Result.Fail<GenerateE2KCorpusData>(
                string.Join(" ", validationErrors));
        }

        var fullOutputDir = Path.GetFullPath(outputDir);
        foreach (var corpusCase in request.Cases)
        {
            var caseDir = Path.Combine(fullOutputDir, corpusCase.Id);
            if (!request.Overwrite &&
                Directory.Exists(caseDir) &&
                Directory.EnumerateFileSystemEntries(caseDir).Any())
            {
                return Result.Fail<GenerateE2KCorpusData>(
                    $"Corpus case output already exists: {caseDir}. Set overwrite=true to replace it.");
            }
        }

        Directory.CreateDirectory(fullOutputDir);

        ETABSApplication? app = null;
        var totalStopwatch = Stopwatch.StartNew();
        try
        {
            Console.Error.WriteLine("Starting ETABS corpus session (hidden)...");
            app = ETABSWrapper.CreateNew(request.EtabsProgramPath);
            if (app is null)
            {
                return Result.Fail<GenerateE2KCorpusData>(
                    "Failed to start ETABS hidden instance.");
            }

            EtabsSessionHelpers.HideIfVisible(app);
            Console.Error.WriteLine(
                $"ETABS corpus session started (v{app.FullVersion}).");

            var generatedCases = new List<CorpusCaseData>(request.Cases.Count);
            foreach (var corpusCase in request.Cases)
            {
                if (app.MajorVersion != corpusCase.ExpectedEtabsMajorVersion)
                {
                    return Result.Fail<GenerateE2KCorpusData>(
                        $"Case '{corpusCase.Id}' requires ETABS v{corpusCase.ExpectedEtabsMajorVersion}, " +
                        $"but the active sidecar instance is v{app.FullVersion}.");
                }

                var generated = await GenerateCaseAsync(
                    app,
                    fullOutputDir,
                    corpusCase,
                    request.ParseBudgetMs);
                generatedCases.Add(generated);
            }

            totalStopwatch.Stop();
            return Result.Ok(new GenerateE2KCorpusData
            {
                OutputDir = fullOutputDir,
                EtabsVersion = app.FullVersion,
                Cases = generatedCases,
                TotalElapsedMs = totalStopwatch.ElapsedMilliseconds
            });
        }
        catch (Exception exception)
        {
            return Result.Fail<GenerateE2KCorpusData>(
                $"ETABS corpus generation failed: {exception.Message}");
        }
        finally
        {
            app?.Application.ApplicationExit(false);
            app?.Dispose();
        }
    }

    private static async Task<CorpusCaseData> GenerateCaseAsync(
        ETABSApplication app,
        string outputDir,
        CorpusCaseRequest corpusCase,
        int parseBudgetMs)
    {
        var stopwatch = Stopwatch.StartNew();
        var planned = PairwiseCorpusPlanner.Plan(corpusCase);
        var caseDir = Path.Combine(outputDir, corpusCase.Id);
        Directory.CreateDirectory(caseDir);

        Console.Error.WriteLine($"Generating corpus case {corpusCase.Id}...");
        var units = corpusCase.Units == CorpusUnits.Metric
            ? eUnits.kN_m_C
            : eUnits.kip_ft_F;

        app.Model.ModelInfo.InitializeNewModel(units);
        app.Model.Files.NewGridOnlyModel(
            planned.Stories,
            planned.StoryHeight,
            planned.StoryHeight,
            planned.GridLinesX,
            planned.GridLinesY,
            planned.GridSpacing,
            planned.GridSpacing);

        var materials = ResolveMaterials(app, corpusCase.Material);
        DefineProperties(app, planned, materials);
        EnsureLoadPatterns(app);
        CreateGeometry(app, planned);

        var edbFile = Path.Combine(caseDir, "model.edb");
        var e2kFile = Path.Combine(caseDir, "model.e2k");
        var manifestFile = Path.Combine(caseDir, "model.meta.toml");

        app.Model.Files.SaveFile(edbFile);
        app.Model.Files.ExportFile(e2kFile, eFileTypeIO.TextFile);
        if (!File.Exists(edbFile) || !File.Exists(e2kFile))
        {
            throw new InvalidOperationException(
                $"ETABS did not create both EDB and E2K artifacts for '{corpusCase.Id}'.");
        }

        var physicalCounts = new CorpusModelCounts
        {
            Stories = app.Model.Story.GetNameList().Length,
            Materials = app.Model.Materials.Count(),
            Points = app.Model.Points.Count(),
            Frames = app.Model.Frames.Count(),
            Areas = app.Model.Areas.Count(),
            LoadPatterns = app.Model.LoadPatterns.Count()
        };
        var counts = CorpusOracleCounts.Calculate(
            planned,
            physicalCounts.Materials,
            physicalCounts.LoadPatterns);
        var sha256 = Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(e2kFile)));
        var manifest = CorpusManifestFormatter.Format(
            planned,
            counts,
            app.FullVersion,
            parseBudgetMs,
            sha256);
        await File.WriteAllTextAsync(manifestFile, manifest);

        stopwatch.Stop();
        Console.Error.WriteLine(
            $"Generated {corpusCase.Id}: {physicalCounts.Points} physical points, " +
            $"{physicalCounts.Frames} physical frames, {physicalCounts.Areas} physical areas; " +
            $"E2K oracle {counts.Points}/{counts.Frames}/{counts.Areas} " +
            $"({stopwatch.ElapsedMilliseconds} ms).");

        return new CorpusCaseData
        {
            Id = corpusCase.Id,
            EdbFile = edbFile,
            E2KFile = e2kFile,
            ManifestFile = manifestFile,
            Counts = counts,
            GenerationTimeMs = stopwatch.ElapsedMilliseconds
        };
    }

    private static CorpusMaterials ResolveMaterials(
        ETABSApplication app,
        CorpusMaterial materialMode)
    {
        var concrete = app.Model.Materials
            .GetNameList(eMatType.Concrete)
            .FirstOrDefault();
        var steel = app.Model.Materials
            .GetNameList(eMatType.Steel)
            .FirstOrDefault();

        return materialMode switch
        {
            CorpusMaterial.Concrete when concrete is not null =>
                new CorpusMaterials(concrete, concrete, concrete),
            CorpusMaterial.Steel when steel is not null =>
                new CorpusMaterials(steel, steel, steel),
            CorpusMaterial.Mixed when concrete is not null && steel is not null =>
                new CorpusMaterials(concrete, steel, concrete),
            CorpusMaterial.Concrete =>
                throw new InvalidOperationException(
                    "The ETABS template did not provide a concrete material."),
            CorpusMaterial.Steel =>
                throw new InvalidOperationException(
                    "The ETABS template did not provide a steel material."),
            CorpusMaterial.Mixed =>
                throw new InvalidOperationException(
                    "The ETABS template did not provide both concrete and steel materials."),
            _ => throw new ArgumentOutOfRangeException(nameof(materialMode))
        };
    }

    private static void DefineProperties(
        ETABSApplication app,
        PlannedCorpusCase planned,
        CorpusMaterials materials)
    {
        app.Model.PropFrame.AddRectangularSection(
            ColumnSection,
            materials.Column,
            planned.FrameDepth,
            planned.FrameWidth);
        app.Model.PropFrame.AddRectangularSection(
            BeamSection,
            materials.Beam,
            planned.FrameDepth * 0.8,
            planned.FrameWidth * 0.65);
        app.Model.PropFrame.AddRectangularSection(
            BraceSection,
            materials.Beam,
            planned.FrameDepth * 0.45,
            planned.FrameWidth * 0.45);
        app.Model.PropArea.SetSlab(
            SlabPropertyName,
            SlabPropertyModel(planned, materials.Area));
        app.Model.PropArea.SetWall(
            WallPropertyName,
            WallPropertyModel(planned, materials.Area));
    }

    private static SlabProperty SlabPropertyModel(
        PlannedCorpusCase planned,
        string material) =>
        SlabProperty.CreateFlat(
            SlabPropertyName,
            material,
            planned.SlabThickness,
            eShellType.ShellThin);

    private static WallProperty WallPropertyModel(
        PlannedCorpusCase planned,
        string material) =>
        WallProperty.CreateStandard(
            WallPropertyName,
            material,
            planned.WallThickness,
            eShellType.ShellThin);

    private static void EnsureLoadPatterns(ETABSApplication app)
    {
        var existing = new HashSet<string>(
            app.Model.LoadPatterns.GetNameList(),
            StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains("DEAD"))
        {
            app.Model.LoadPatterns.Add("DEAD", eLoadPatternType.Dead, 1.0);
        }

        if (!existing.Contains("LIVE"))
        {
            app.Model.LoadPatterns.Add("LIVE", eLoadPatternType.Live);
        }
    }

    private static void CreateGeometry(
        ETABSApplication app,
        PlannedCorpusCase planned)
    {
        for (var story = 1; story <= planned.Stories; story++)
        {
            var bottom = (story - 1) * planned.StoryHeight;
            var top = story * planned.StoryHeight;
            AddColumns(app, planned, story, bottom, top);
            AddBeams(app, planned, story, top);
            AddSlabs(app, planned, story, top);

            switch (planned.Source.LateralSystem)
            {
                case CorpusLateralSystem.MomentFrame:
                    break;
                case CorpusLateralSystem.BracedFrame:
                    AddBrace(app, planned, story, bottom, top);
                    break;
                case CorpusLateralSystem.ShearWall:
                    AddWall(app, planned, story, bottom, top);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(planned));
            }
        }
    }

    private static void AddColumns(
        ETABSApplication app,
        PlannedCorpusCase planned,
        int story,
        double bottom,
        double top)
    {
        for (var x = 0; x < planned.GridLinesX; x++)
        {
            for (var y = 0; y < planned.GridLinesY; y++)
            {
                var xCoordinate = x * planned.GridSpacing;
                var yCoordinate = y * planned.GridSpacing;
                app.Model.Frames.AddFrameByCoordinates(
                    xCoordinate,
                    yCoordinate,
                    bottom,
                    xCoordinate,
                    yCoordinate,
                    top,
                    ColumnSection,
                    $"C-S{story:00}-X{x:00}-Y{y:00}");
            }
        }
    }

    private static void AddBeams(
        ETABSApplication app,
        PlannedCorpusCase planned,
        int story,
        double elevation)
    {
        for (var y = 0; y < planned.GridLinesY; y++)
        {
            for (var x = 0; x < planned.GridLinesX - 1; x++)
            {
                app.Model.Frames.AddFrameByCoordinates(
                    x * planned.GridSpacing,
                    y * planned.GridSpacing,
                    elevation,
                    (x + 1) * planned.GridSpacing,
                    y * planned.GridSpacing,
                    elevation,
                    BeamSection,
                    $"BX-S{story:00}-X{x:00}-Y{y:00}");
            }
        }

        for (var x = 0; x < planned.GridLinesX; x++)
        {
            for (var y = 0; y < planned.GridLinesY - 1; y++)
            {
                app.Model.Frames.AddFrameByCoordinates(
                    x * planned.GridSpacing,
                    y * planned.GridSpacing,
                    elevation,
                    x * planned.GridSpacing,
                    (y + 1) * planned.GridSpacing,
                    elevation,
                    BeamSection,
                    $"BY-S{story:00}-X{x:00}-Y{y:00}");
            }
        }
    }

    private static void AddSlabs(
        ETABSApplication app,
        PlannedCorpusCase planned,
        int story,
        double elevation)
    {
        for (var x = 0; x < planned.GridLinesX - 1; x++)
        {
            for (var y = 0; y < planned.GridLinesY - 1; y++)
            {
                var x0 = x * planned.GridSpacing;
                var x1 = (x + 1) * planned.GridSpacing;
                var y0 = y * planned.GridSpacing;
                var y1 = (y + 1) * planned.GridSpacing;
                app.Model.Areas.AddAreaByCoordinates(
                    [
                        new AreaCoordinate(x0, y0, elevation),
                        new AreaCoordinate(x1, y0, elevation),
                        new AreaCoordinate(x1, y1, elevation),
                        new AreaCoordinate(x0, y1, elevation)
                    ],
                    SlabPropertyName,
                    $"SLAB-S{story:00}-X{x:00}-Y{y:00}");
            }
        }
    }

    private static void AddBrace(
        ETABSApplication app,
        PlannedCorpusCase planned,
        int story,
        double bottom,
        double top)
    {
        app.Model.Frames.AddFrameByCoordinates(
            0,
            0,
            bottom,
            planned.GridSpacing,
            0,
            top,
            BraceSection,
            $"BRACE-S{story:00}");
    }

    private static void AddWall(
        ETABSApplication app,
        PlannedCorpusCase planned,
        int story,
        double bottom,
        double top)
    {
        app.Model.Areas.AddAreaByCoordinates(
            [
                new AreaCoordinate(0, 0, bottom),
                new AreaCoordinate(planned.GridSpacing, 0, bottom),
                new AreaCoordinate(planned.GridSpacing, 0, top),
                new AreaCoordinate(0, 0, top)
            ],
            WallPropertyName,
            $"WALL-S{story:00}");
    }

    private sealed record CorpusMaterials(
        string Column,
        string Beam,
        string Area);
}
