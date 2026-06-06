using System.Globalization;
using System.Text;
using EtabExtension.CLI.Features.GenerateE2KCorpus.Models;

namespace EtabExtension.CLI.Features.GenerateE2KCorpus;

public static class CorpusManifestFormatter
{
    public static string Format(
        PlannedCorpusCase planned,
        CorpusModelCounts counts,
        string etabsVersion,
        int parseBudgetMs,
        string sha256)
    {
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentException.ThrowIfNullOrWhiteSpace(etabsVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);

        var source = planned.Source;
        var builder = new StringBuilder();
        AppendString(builder, "case_id", source.Id);
        AppendString(builder, "source_kind", "etabs-api-generated");
        AppendString(builder, "sha256", sha256);
        AppendString(builder, "shape", ToSnakeCase(source.Shape.ToString()));
        AppendString(builder, "material", ToSnakeCase(source.Material.ToString()));
        AppendString(
            builder,
            "lateral_system",
            ToSnakeCase(source.LateralSystem.ToString()));
        AppendString(builder, "grid", ToSnakeCase(source.Grid.ToString()));
        AppendString(builder, "units", ToSnakeCase(source.Units.ToString()));
        AppendString(builder, "etabs_version", etabsVersion);
        builder.AppendLine(
            $"expected_etabs_major_version = {source.ExpectedEtabsMajorVersion}");
        builder.AppendLine($"stories = {counts.Stories}");
        builder.AppendLine($"materials = {counts.Materials}");
        builder.AppendLine($"n_points = {counts.Points}");
        builder.AppendLine($"n_frames = {counts.Frames}");
        builder.AppendLine($"n_areas = {counts.Areas}");
        builder.AppendLine($"n_load_patterns = {counts.LoadPatterns}");
        builder.AppendLine($"parse_budget_ms = {parseBudgetMs}");
        builder.AppendLine(
            $"story_height = {planned.StoryHeight.ToString("R", CultureInfo.InvariantCulture)}");
        builder.AppendLine(
            $"grid_spacing = {planned.GridSpacing.ToString("R", CultureInfo.InvariantCulture)}");
        return builder.ToString();
    }

    private static void AppendString(StringBuilder builder, string key, string value) =>
        builder.AppendLine($"{key} = \"{EscapeToml(value)}\"");

    private static string EscapeToml(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string ToSnakeCase(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
