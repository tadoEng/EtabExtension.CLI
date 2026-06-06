using EtabExtension.CLI.Features.GenerateE2KCorpus.Models;

namespace EtabExtension.CLI.Features.GenerateE2KCorpus;

public static class CorpusOracleCounts
{
    public static CorpusModelCounts Calculate(
        PlannedCorpusCase planned,
        int materialCount,
        int loadPatternCount)
    {
        ArgumentNullException.ThrowIfNull(planned);

        var columns = planned.GridLinesX * planned.GridLinesY;
        var xBeams = (planned.GridLinesX - 1) * planned.GridLinesY;
        var yBeams = planned.GridLinesX * (planned.GridLinesY - 1);
        var braces = planned.Source.LateralSystem == CorpusLateralSystem.BracedFrame
            ? 1
            : 0;
        var slabs = (planned.GridLinesX - 1) * (planned.GridLinesY - 1);
        var walls = planned.Source.LateralSystem == CorpusLateralSystem.ShearWall
            ? 1
            : 0;

        return new CorpusModelCounts
        {
            Stories = planned.Stories + 1,
            Materials = materialCount,
            Points = columns,
            Frames = columns + xBeams + yBeams + braces,
            Areas = slabs + walls,
            LoadPatterns = loadPatternCount
        };
    }
}
