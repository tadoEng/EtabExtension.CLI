namespace EtabExtension.CLI.Features.GenerateE2KCorpus.Models;

public enum CorpusShape
{
    Compact,
    MidRise,
    Tower
}

public enum CorpusMaterial
{
    Concrete,
    Steel,
    Mixed
}

public enum CorpusLateralSystem
{
    MomentFrame,
    BracedFrame,
    ShearWall
}

public enum CorpusGrid
{
    TwoByTwo,
    ThreeByThree
}

public enum CorpusUnits
{
    Us,
    Metric
}

public record CorpusCaseRequest
{
    public string Id { get; init; } = string.Empty;
    public CorpusShape Shape { get; init; }
    public CorpusMaterial Material { get; init; }
    public CorpusLateralSystem LateralSystem { get; init; }
    public CorpusGrid Grid { get; init; }
    public CorpusUnits Units { get; init; }
    public int ExpectedEtabsMajorVersion { get; init; }
}

public record GenerateE2KCorpusRequest
{
    public List<CorpusCaseRequest> Cases { get; init; } = [];
    public bool Overwrite { get; init; }
    public int ParseBudgetMs { get; init; } = 250;
    public string? EtabsProgramPath { get; init; }
}

public record PlannedCorpusCase
{
    public required CorpusCaseRequest Source { get; init; }
    public int Stories { get; init; }
    public int GridLinesX { get; init; }
    public int GridLinesY { get; init; }
    public double StoryHeight { get; init; }
    public double GridSpacing { get; init; }
    public double FrameDepth { get; init; }
    public double FrameWidth { get; init; }
    public double SlabThickness { get; init; }
    public double WallThickness { get; init; }
    public int ExpectedEtabsMajorVersion => Source.ExpectedEtabsMajorVersion;
}

public record CorpusModelCounts
{
    public int Stories { get; init; }
    public int Materials { get; init; }
    public int Points { get; init; }
    public int Frames { get; init; }
    public int Areas { get; init; }
    public int LoadPatterns { get; init; }
}

public record CorpusCaseData
{
    public string Id { get; init; } = string.Empty;
    public string EdbFile { get; init; } = string.Empty;
    public string E2KFile { get; init; } = string.Empty;
    public string ManifestFile { get; init; } = string.Empty;
    public CorpusModelCounts Counts { get; init; } = new();
    public long GenerationTimeMs { get; init; }
}

public record GenerateE2KCorpusData
{
    public string OutputDir { get; init; } = string.Empty;
    public string EtabsVersion { get; init; } = string.Empty;
    public List<CorpusCaseData> Cases { get; init; } = [];
    public long TotalElapsedMs { get; init; }
}
