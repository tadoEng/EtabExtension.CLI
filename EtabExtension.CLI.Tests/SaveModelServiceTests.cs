using EtabExtension.CLI.Features.SaveModel;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// <c>save-model</c> saves to the EXPLICIT path it was given, and only when that path
/// is the model ETABS has open (EtabExtension #443). Saving under ETABS's current name
/// is what overwrote a committed version: that name read <c>vN\model.e2k</c> after an
/// export and <c>vN\model.$et</c> after a UI Ctrl+S on ETABS 23.3.
/// </summary>
public sealed class SaveModelServiceTests
{
    private const string Working = @"D:\Project\.etabs-ext\main\working\model.edb";

    [Fact]
    public void SavesToTheRequestedPathWhenThatModelIsOpen()
    {
        var api = new FakeModelFileApi { CurrentPath = Working };

        var result = Save(Working, api);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Working, api.SavedPath);
        Assert.Equal(Working, result.Data!.FilePath);
        Assert.Equal(Working, result.Data.ReportedBefore);
    }

    [Fact]
    public void AcceptsTheLockSiblingEtabsReportsAfterAUiSave()
    {
        // Observed live: after Ctrl+S ETABS named its document model.$et.
        var api = new FakeModelFileApi
        {
            CurrentPath = @"D:\Project\.etabs-ext\main\working\model.$et",
        };

        var result = Save(Working, api);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Working, api.SavedPath);
    }

    [Theory]
    [InlineData(@"D:\Project\.etabs-ext\main\v2\model.e2k")]
    [InlineData(@"D:\Project\.etabs-ext\main\v2\model.edb")]
    [InlineData(@"D:\Project\.etabs-ext\main\v2\model.$et")]
    [InlineData(@"D:\Project\.etabs-ext\main\working\")]
    [InlineData("")]
    [InlineData(null)]
    public void RefusesWhenAnythingElseIsOpenAndNeverCallsSave(string? current)
    {
        var api = new FakeModelFileApi { CurrentPath = current };

        var result = Save(Working, api);

        Assert.False(result.Success);
        Assert.Contains("Refusing to save", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, api.SaveCalls);
    }

    [Fact]
    public void ANonZeroSaveReturnFailsAndNamesTheCsiCall()
    {
        var api = new FakeModelFileApi { CurrentPath = Working, SaveReturn = 1 };

        var result = Save(Working, api);

        Assert.False(result.Success);
        Assert.Contains(SaveModelService.SaveOperation, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AThrowingSaveFailsAndNamesTheCsiCall()
    {
        // EtabSharp throws on a non-zero return instead of returning it.
        var api = new FakeModelFileApi { CurrentPath = Working, ThrowOnSave = true };

        var result = Save(Working, api);

        Assert.False(result.Success);
        Assert.Contains(SaveModelService.SaveOperation, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ADifferentSpellingOfTheSameFileIsAcceptedOnlyByIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "etab-cli-save-model", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var reported = Path.Combine(directory, "alias.edb");
            File.WriteAllText(reported, "fixture");

            Assert.Null(SaveModelService.ValidateOpenModelIs(
                reported,
                Working,
                (_, _) => new FileIdentityResult(FileIdentityMatch.Same, 0)));
            Assert.NotNull(SaveModelService.ValidateOpenModelIs(
                reported,
                Working,
                (_, _) => new FileIdentityResult(FileIdentityMatch.Different, 0)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Shared.Common.Result<Features.SaveModel.Models.SaveModelData> Save(
        string filePath,
        FakeModelFileApi api) => SaveModelService.SaveOnApi(
            api,
            filePath,
            (_, _) => new FileIdentityResult(FileIdentityMatch.Different, 0));

    private sealed class FakeModelFileApi : IEtabsModelFileApi
    {
        public string? CurrentPath { get; set; }
        public string? SavedPath { get; private set; }
        public int SaveCalls { get; private set; }
        public int SaveReturn { get; init; }
        public bool ThrowOnSave { get; init; }

        public string? GetModelFilename() => CurrentPath;

        public int SaveFile(string filePath)
        {
            SaveCalls++;
            if (ThrowOnSave)
            {
                throw new InvalidOperationException("SaveFile failed");
            }

            SavedPath = filePath;
            return SaveReturn;
        }

        public int OpenFile(string filePath) =>
            throw new InvalidOperationException("save-model must never open a model");
    }
}
