using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metadata;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.ReadModelMetadata;

public interface IReadModelMetadataService
{
    Task<Result<ModelMetadata>> ReadOnAppAsync(ETABSApplication app, string filePath);
}
