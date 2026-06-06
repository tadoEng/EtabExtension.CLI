using System.Text.RegularExpressions;
using EtabExtension.CLI.Features.GenerateE2KCorpus.Models;

namespace EtabExtension.CLI.Features.GenerateE2KCorpus;

public static partial class CorpusRequestValidator
{
    public static IReadOnlyList<string> Validate(GenerateE2KCorpusRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();
        if (request.Cases.Count == 0)
        {
            errors.Add("At least one corpus case is required.");
        }

        if (request.ParseBudgetMs <= 0)
        {
            errors.Add("ParseBudgetMs must be positive.");
        }

        if (!string.IsNullOrWhiteSpace(request.EtabsProgramPath) &&
            !File.Exists(request.EtabsProgramPath))
        {
            errors.Add(
                $"ETABS executable does not exist: {request.EtabsProgramPath}");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var corpusCase in request.Cases)
        {
            if (string.IsNullOrWhiteSpace(corpusCase.Id) ||
                !SafeCaseId().IsMatch(corpusCase.Id))
            {
                errors.Add(
                    $"Case id '{corpusCase.Id}' must be a safe file name using letters, numbers, '.', '_', or '-'.");
            }

            if (!ids.Add(corpusCase.Id))
            {
                errors.Add($"Case id '{corpusCase.Id}' must be unique.");
            }

            if (corpusCase.ExpectedEtabsMajorVersion <= 0)
            {
                errors.Add(
                    $"Case '{corpusCase.Id}' must declare a positive expected ETABS major version.");
            }
        }

        return errors;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCaseId();
}
