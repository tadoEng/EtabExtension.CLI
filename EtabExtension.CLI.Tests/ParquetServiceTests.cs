using System.Text.Json;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using Parquet;
using Xunit;

namespace EtabExtension.CLI.Tests;

public class ParquetServiceTests
{
    private const string ColumnMappingMetadataKey = "etabextension.cli.parquet.columnMapping.v1";

    [Fact]
    public async Task WriteAsyncStoresOriginalFieldNameMappingInFileMetadata()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "etabextension-cli-tests", Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(tempDir, "table.parquet");

        try
        {
            var fieldNames = new List<string>
            {
                "Story Level",
                "Story/Level",
                "Base Reaction (FX)",
                "Base Reaction [FX]",
                "   ",
                "   "
            };
            var flatData = new List<string>
            {
                "Roof", "Level 10", "100", "101", "blank-1", "blank-2",
                "Base", "Level 01", "200", "201", "blank-3", "blank-4"
            };

            var result = await new ParquetService().WriteAsync(outputPath, fieldNames, flatData);

            Assert.True(result.Success, result.Error);
            Assert.Collection(
                result.Columns ?? [],
                column => AssertResultColumnMapping(column, 0, "Story_Level", "Story Level"),
                column => AssertResultColumnMapping(column, 1, "Story_Level_2", "Story/Level"),
                column => AssertResultColumnMapping(column, 2, "Base_Reaction__FX", "Base Reaction (FX)"),
                column => AssertResultColumnMapping(column, 3, "Base_Reaction__FX_2", "Base Reaction [FX]"),
                column => AssertResultColumnMapping(column, 4, "Column", "   "),
                column => AssertResultColumnMapping(column, 5, "Column_2", "   "));

            using var reader = await ParquetReader.CreateAsync(
                outputPath,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(
                [
                    "Story_Level",
                    "Story_Level_2",
                    "Base_Reaction__FX",
                    "Base_Reaction__FX_2",
                    "Column",
                    "Column_2"
                ],
                reader.Schema.GetDataFields().Select(field => field.Name));

            Assert.True(
                reader.CustomMetadata.TryGetValue(ColumnMappingMetadataKey, out var mappingJson),
                $"Expected parquet file metadata key '{ColumnMappingMetadataKey}'.");

            using var document = JsonDocument.Parse(mappingJson);
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());

            var columns = root.GetProperty("columns");
            Assert.Equal(fieldNames.Count, columns.GetArrayLength());

            AssertColumnMapping(columns[0], 0, "Story_Level", "Story Level");
            AssertColumnMapping(columns[1], 1, "Story_Level_2", "Story/Level");
            AssertColumnMapping(columns[2], 2, "Base_Reaction__FX", "Base Reaction (FX)");
            AssertColumnMapping(columns[3], 3, "Base_Reaction__FX_2", "Base Reaction [FX]");
            AssertColumnMapping(columns[4], 4, "Column", "   ");
            AssertColumnMapping(columns[5], 5, "Column_2", "   ");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static void AssertColumnMapping(
        JsonElement column,
        int index,
        string parquetColumnName,
        string originalFieldName)
    {
        Assert.Equal(index, column.GetProperty("index").GetInt32());
        Assert.Equal(parquetColumnName, column.GetProperty("parquetColumnName").GetString());
        Assert.Equal(originalFieldName, column.GetProperty("originalFieldName").GetString());
    }

    private static void AssertResultColumnMapping(
        ParquetColumnMapping column,
        int index,
        string parquetColumnName,
        string originalFieldName)
    {
        Assert.Equal(index, column.Index);
        Assert.Equal(parquetColumnName, column.ParquetColumnName);
        Assert.Equal(originalFieldName, column.OriginalFieldName);
    }
}
