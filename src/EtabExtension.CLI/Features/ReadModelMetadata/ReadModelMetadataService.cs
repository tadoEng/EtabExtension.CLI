using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metadata;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.ReadModelMetadata;

public sealed class ReadModelMetadataService : IReadModelMetadataService
{
    public async Task<Result<ModelMetadata>> ReadOnAppAsync(ETABSApplication app, string filePath)
    {
        if (!File.Exists(filePath)) return Result.Fail<ModelMetadata>($"File not found: {filePath}");
        try
        {
            var open = await EtabsSessionHelpers.OpenFileAsync(app, filePath);
            if (!open.Success) return Result.Fail<ModelMetadata>(open.Error ?? "OpenFile failed");
            var current = await new EtabsUnitService(app).ReadCurrentAsync();
            var snapshot = new UnitSnapshot { Original = current, Active = current, WasChanged = false };
            return Result.Ok(await EtabsSessionHelpers.CollectModelMetadataAsync(app, filePath, snapshot));
        }
        catch (Exception ex) { return Result.Fail<ModelMetadata>($"ETABS COM error: {ex.Message}"); }
    }
}
