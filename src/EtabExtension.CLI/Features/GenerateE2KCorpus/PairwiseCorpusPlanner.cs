using EtabExtension.CLI.Features.GenerateE2KCorpus.Models;

namespace EtabExtension.CLI.Features.GenerateE2KCorpus;

public static class PairwiseCorpusPlanner
{
    private const int CorpusSize = 36;

    public static List<CorpusCaseRequest> BuildDefaultCases()
    {
        var candidates = AllCandidates();
        var uncovered = AllPairs();
        var selected = new List<CorpusCaseRequest>();

        while (uncovered.Count > 0)
        {
            var best = candidates
                .Where(candidate => !selected.Contains(candidate))
                .OrderByDescending(candidate => CasePairs(candidate).Count(uncovered.Contains))
                .ThenBy(CaseKey, StringComparer.Ordinal)
                .First();

            uncovered.ExceptWith(CasePairs(best));
            selected.Add(best);
        }

        foreach (var candidate in candidates)
        {
            if (selected.Count == CorpusSize)
            {
                break;
            }

            if (!selected.Contains(candidate))
            {
                selected.Add(candidate);
            }
        }

        return selected
            .Select((item, index) => item with { Id = $"case-{index + 1:000}" })
            .ToList();
    }

    public static List<CorpusCaseRequest> BuildDefaultCasesForVersion(
        int expectedEtabsMajorVersion)
    {
        if (expectedEtabsMajorVersion is not 22 and not 23)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedEtabsMajorVersion),
                "The v0.2 corpus plan supports ETABS major versions 22 and 23.");
        }

        return BuildDefaultCases()
            .Where(item =>
                item.ExpectedEtabsMajorVersion == expectedEtabsMajorVersion)
            .ToList();
    }

    public static PlannedCorpusCase Plan(CorpusCaseRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        if (request.ExpectedEtabsMajorVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Expected ETABS major version must be positive.");
        }

        var metric = request.Units == CorpusUnits.Metric;
        return new PlannedCorpusCase
        {
            Source = request,
            Stories = request.Shape switch
            {
                CorpusShape.Compact => 4,
                CorpusShape.MidRise => 7,
                CorpusShape.Tower => 11,
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            },
            GridLinesX = request.Grid == CorpusGrid.TwoByTwo ? 2 : 3,
            GridLinesY = request.Grid == CorpusGrid.TwoByTwo ? 2 : 3,
            StoryHeight = metric ? 3.6 : 12.0,
            GridSpacing = metric ? 8.0 : 24.0,
            FrameDepth = metric ? 0.6 : 2.0,
            FrameWidth = metric ? 0.6 : 2.0,
            SlabThickness = metric ? 0.2 : 0.67,
            WallThickness = metric ? 0.3 : 1.0
        };
    }

    public static IReadOnlyCollection<string> FindUncoveredPairs(
        IReadOnlyCollection<CorpusCaseRequest> cases)
    {
        var uncovered = AllPairs();
        foreach (var item in cases)
        {
            uncovered.ExceptWith(CasePairs(item));
        }

        return uncovered.Order(StringComparer.Ordinal).ToArray();
    }

    private static List<CorpusCaseRequest> AllCandidates()
    {
        var candidates = new List<CorpusCaseRequest>();
        foreach (var shape in Enum.GetValues<CorpusShape>())
        {
            foreach (var material in Enum.GetValues<CorpusMaterial>())
            {
                foreach (var lateral in Enum.GetValues<CorpusLateralSystem>())
                {
                    foreach (var grid in Enum.GetValues<CorpusGrid>())
                    {
                        foreach (var units in Enum.GetValues<CorpusUnits>())
                        {
                            foreach (var version in new[] { 22, 23 })
                            {
                                candidates.Add(new CorpusCaseRequest
                                {
                                    Shape = shape,
                                    Material = material,
                                    LateralSystem = lateral,
                                    Grid = grid,
                                    Units = units,
                                    ExpectedEtabsMajorVersion = version
                                });
                            }
                        }
                    }
                }
            }
        }

        return candidates;
    }

    private static HashSet<string> AllPairs()
    {
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        var values = new[]
        {
            Enum.GetNames<CorpusShape>(),
            Enum.GetNames<CorpusMaterial>(),
            Enum.GetNames<CorpusLateralSystem>(),
            Enum.GetNames<CorpusGrid>(),
            Enum.GetNames<CorpusUnits>(),
            new[] { "22", "23" }
        };

        for (var left = 0; left < values.Length; left++)
        {
            for (var right = left + 1; right < values.Length; right++)
            {
                foreach (var leftValue in values[left])
                {
                    foreach (var rightValue in values[right])
                    {
                        pairs.Add(PairKey(left, leftValue, right, rightValue));
                    }
                }
            }
        }

        return pairs;
    }

    private static HashSet<string> CasePairs(CorpusCaseRequest item)
    {
        var values = new[]
        {
            item.Shape.ToString(),
            item.Material.ToString(),
            item.LateralSystem.ToString(),
            item.Grid.ToString(),
            item.Units.ToString(),
            item.ExpectedEtabsMajorVersion.ToString()
        };
        var pairs = new HashSet<string>(StringComparer.Ordinal);

        for (var left = 0; left < values.Length; left++)
        {
            for (var right = left + 1; right < values.Length; right++)
            {
                pairs.Add(PairKey(left, values[left], right, values[right]));
            }
        }

        return pairs;
    }

    private static string PairKey(int left, string leftValue, int right, string rightValue) =>
        $"{left}:{leftValue}|{right}:{rightValue}";

    private static string CaseKey(CorpusCaseRequest item) =>
        $"{(int)item.Shape}:{(int)item.Material}:{(int)item.LateralSystem}:" +
        $"{(int)item.Grid}:{(int)item.Units}:{item.ExpectedEtabsMajorVersion}";
}
