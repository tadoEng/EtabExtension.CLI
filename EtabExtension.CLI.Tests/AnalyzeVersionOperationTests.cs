using System.Text.Json;
using EtabExtension.CLI.Features.AnalyzeVersion;
using EtabExtension.CLI.Features.AnalyzeVersion.Models;
using EtabExtension.CLI.Features.GetStatus;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabSharp.Core;
using ETABSv1;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// The operation shell of <c>analyze-version</c> (request refusal before any COM call, no
/// cold start, payload mapping) and the checked unit maps (analyze-version card C-8, C-10).
/// </summary>
public sealed class AnalyzeVersionOperationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "etab-cli-analyze-version-op-tests", Guid.NewGuid().ToString("N"));
    private OperationManager? _manager;

    public void Dispose()
    {
        _manager?.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ---------------------------------------------------------------- C-8

    /// <summary>
    /// Catches: mapping cm to m (or any token to a neighbour's value), a raw value that drifts
    /// from the ETABSv1 enum, or accepting an unknown or wrongly-cased token.
    /// </summary>
    [Fact]
    public void Every_force_and_length_token_maps_to_one_enum_value_and_back()
    {
        var examined = 0;

        foreach (var token in CheckedUnits.ForceTokens)
        {
            Assert.True(CheckedUnits.TryForceToRaw(token, out var raw));
            Assert.Equal((int)Enum.Parse<eForce>(token), raw);
            Assert.Equal(token, CheckedUnits.ForceToken(raw));
            examined++;
        }

        foreach (var token in CheckedUnits.LengthTokens)
        {
            // The contract says "in"; the ETABSv1 enum calls it "inch".
            var enumName = token == "in" ? "inch" : token;
            Assert.True(CheckedUnits.TryLengthToRaw(token, out var raw));
            Assert.Equal((int)Enum.Parse<eLength>(enumName), raw);
            Assert.Equal(token, CheckedUnits.LengthToken(raw));
            examined++;
        }

        foreach (var token in CheckedUnits.TemperatureTokens)
        {
            Assert.True(CheckedUnits.TryTemperatureToRaw(token, out var raw));
            Assert.Equal((int)Enum.Parse<eTemperature>(token), raw);
            Assert.Equal(token, CheckedUnits.TemperatureToken(raw));
            examined++;
        }

        Assert.Equal(6 + 5 + 2, examined);

        // No two tokens share an enum value.
        Assert.Equal(6, CheckedUnits.ForceTokens.Select(t => { CheckedUnits.TryForceToRaw(t, out var r); return r; }).Distinct().Count());
        Assert.Equal(5, CheckedUnits.LengthTokens.Select(t => { CheckedUnits.TryLengthToRaw(t, out var r); return r; }).Distinct().Count());

        // Refusals: unknown, wrongly cased, micron, empty, null (6 inputs, each refused as a length AND a force).
        var refused = 0;
        foreach (var token in new[] { "micron", "KN", "Kip", "", null, "CM" })
        {
            Assert.False(CheckedUnits.TryForceToRaw(token, out _), $"force accepted '{token}'");
            Assert.False(CheckedUnits.TryLengthToRaw(token, out _), $"length accepted '{token}'");
            refused++;
        }

        Assert.Equal(6, refused);
        Assert.Null(CheckedUnits.FromTokens("kN", "furlong", null, out var error));
        Assert.Contains("furlong", error, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------- C-10

    /// <summary>
    /// Catches: defaulting the policy, the units or the document; touching the session or ETABS
    /// before a request is refused. Three refusals, zero session touches, zero seam creations.
    /// </summary>
    [Fact]
    public async Task A_request_without_units_policy_or_expected_document_is_refused()
    {
        var file = CreateScratchFile();
        var session = new FakeSession();
        var factory = new CountingFactory();
        var service = new RecordingService();
        var manager = CreateManager(session, service, factory);

        var common = $$"""
            "filePath":{{Json(file)}},"outputDir":{{Json(_directory)}},"extractionProfile":"full"
            """;
        var document = $$"""
            "expectedDocument":{"path":{{Json(file)}},"fileId":"id"}
            """;
        const string units = """
            "expectedUnits":{"force":"kip","length":"ft"}
            """;
        const string policy = """
            "unitPolicy":"setAndRestore"
            """;
        var cases = new[]
        {
            ("no expectedUnits", "{" + string.Join(",", common, document, policy) + "}"),
            ("no unitPolicy", "{" + string.Join(",", common, document, units) + "}"),
            ("no expectedDocument", "{" + string.Join(",", common, units, policy) + "}")
        };

        foreach (var (name, payload) in cases)
        {
            var started = manager.Start("analyze-version", JsonSerializer.Deserialize<JsonElement>(payload));
            var result = Assert.IsType<Result<AnalyzeVersionData>>(await manager.WaitAsync(
                started.Data!.OperationId, TestContext.Current.CancellationToken));
            Assert.False(result.Success, name);
            Assert.StartsWith("Invalid analyze-version request:", result.Error, StringComparison.Ordinal);
        }

        Assert.Equal(3, cases.Length);
        Assert.Equal(0, session.GetOrStartCalls);
        Assert.Equal(0, factory.Created);
        Assert.Equal(0, service.Calls);
    }

    /// <summary>
    /// Catches: accepting an unknown policy, unknown unit token or a missing file id (the
    /// pure validator: no defaults).
    /// </summary>
    [Fact]
    public void The_validator_has_no_defaults()
    {
        static AnalyzeVersionRequest Valid() => new()
        {
            FilePath = "a.edb",
            OutputDir = "out",
            ExpectedDocument = new AnalyzeVersionExpectedDocument { Path = "a.edb", FileId = "id" },
            ExpectedUnits = new AnalyzeVersionExpectedUnits { Force = "kN", Length = "m" },
            UnitPolicy = "checkOnly"
        };

        Assert.NotNull(AnalyzeVersionRequestValidator.Validate(Valid(), out _));
        Assert.NotNull(AnalyzeVersionRequestValidator.Validate(
            Valid() with { UnitPolicy = "setAndRestore" }, out _));

        // The same file spelled differently (case) is still the expected document.
        Assert.NotNull(AnalyzeVersionRequestValidator.Validate(Valid() with { FilePath = "A.EDB" }, out _));

        var refused = new[]
        {
            Valid() with { UnitPolicy = "verifyOnly" },
            Valid() with { UnitPolicy = "CHECKONLY" },
            Valid() with { ExpectedUnits = new AnalyzeVersionExpectedUnits { Force = "KN", Length = "m" } },
            Valid() with { ExpectedUnits = new AnalyzeVersionExpectedUnits { Force = "kN", Length = "micron" } },
            Valid() with { ExpectedUnits = new AnalyzeVersionExpectedUnits { Force = "kN", Length = "m", Temperature = "K" } },
            Valid() with { ExpectedDocument = new AnalyzeVersionExpectedDocument { Path = "a.edb" } },
            Valid() with { OutputDir = " " },
            // AV-L7 SF3: the analysed file must be the expected document.
            Valid() with { FilePath = "b.edb" }
        };
        foreach (var request in refused)
        {
            Assert.Null(AnalyzeVersionRequestValidator.Validate(request, out var error));
            Assert.NotNull(error);
        }

        Assert.Equal(8, refused.Length);
    }

    /// <summary>
    /// Catches (AV-L7 SF1): running against an instance the user can see, under either policy;
    /// touching the service or the seam before refusing. Two policies, zero COM-side calls.
    /// </summary>
    [Fact]
    public async Task A_user_visible_instance_is_refused_before_any_com_call()
    {
        var file = CreateScratchFile();
        var cases = 0;
        foreach (var policy in new[] { "checkOnly", "setAndRestore" })
        {
            var session = new FakeSession { IsUserVisible = true };
            var factory = new CountingFactory();
            var service = new RecordingService();
            var manager = CreateManager(session, service, factory);
            var payload = JsonSerializer.SerializeToElement(new
            {
                filePath = file,
                outputDir = _directory,
                expectedDocument = new { path = file, fileId = "id" },
                expectedUnits = new { force = "kip", length = "ft" },
                unitPolicy = policy,
                extractionProfile = "full"
            });

            var started = manager.Start("analyze-version", payload);
            var result = Assert.IsType<Result<AnalyzeVersionData>>(await manager.WaitAsync(
                started.Data!.OperationId, TestContext.Current.CancellationToken));

            Assert.False(result.Success, policy);
            Assert.StartsWith("Invalid analyze-version request:", result.Error, StringComparison.Ordinal);
            Assert.Contains("visible", result.Error, StringComparison.Ordinal);
            Assert.Equal(0, session.GetOrStartCalls);
            Assert.Equal(0, factory.Created);
            Assert.Equal(0, service.Calls);
            cases++;
            manager.Dispose();
            _manager = null;
        }

        Assert.Equal(2, cases);
    }

    /// <summary>Catches: cold-starting ETABS (or touching the session) when none is running.</summary>
    [Fact]
    public async Task Analyze_version_never_starts_etabs()
    {
        var file = CreateScratchFile();
        var session = new FakeSession(isStarted: false);
        var factory = new CountingFactory();
        var service = new RecordingService();
        var manager = CreateManager(session, service, factory);

        var started = manager.Start("analyze-version", ValidPayload(file));
        var result = Assert.IsType<Result<AnalyzeVersionData>>(await manager.WaitAsync(
            started.Data!.OperationId, TestContext.Current.CancellationToken));

        Assert.False(result.Success);
        Assert.StartsWith("[ANALYSIS_NOT_STARTED]", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, session.GetOrStartCalls);
        Assert.Equal(0, factory.Created);
        Assert.Equal(0, service.Calls);
    }

    /// <summary>Catches: dropping or renaming a request member on the way to the service.</summary>
    [Fact]
    public async Task The_operation_forwards_the_ten_request_members_to_the_service()
    {
        var file = CreateScratchFile();
        var session = new FakeSession();
        var factory = new CountingFactory();
        var service = new RecordingService();
        var manager = CreateManager(session, service, factory);
        var payload = JsonSerializer.Deserialize<JsonElement>(
            $$$"""
            {"filePath":{{{Json(file)}}},"outputDir":{{{Json(_directory)}}},
             "expectedDocument":{"path":{{{Json(file)}}},"fileId":"id-9"},
             "expectedUnits":{"force":"kN","length":"mm","temperature":"C"},
             "unitPolicy":"setAndRestore","cases":["DEAD","LIVE"],
             "tables":{"baseReactions":{"loadCases":["*"]}},"extractionProfile":"results",
             "metadataOutputPath":"m.json","metricsOutputPath":"r.json"}
            """);

        var started = manager.Start("analyze-version", payload);
        var result = Assert.IsType<Result<AnalyzeVersionData>>(await manager.WaitAsync(
            started.Data!.OperationId, TestContext.Current.CancellationToken));

        Assert.True(result.Success, result.Error);
        var request = service.Request!;
        Assert.Equal(file, request.FilePath);
        Assert.Equal(_directory, request.OutputDir);
        Assert.Equal("id-9", request.ExpectedDocument!.FileId);
        Assert.Equal(("kN", "mm", "C"), (request.ExpectedUnits!.Force, request.ExpectedUnits.Length, request.ExpectedUnits.Temperature));
        Assert.Equal("setAndRestore", request.UnitPolicy);
        Assert.Equal(["DEAD", "LIVE"], request.Cases);
        Assert.NotNull(request.Tables!.BaseReactions);
        Assert.Equal("results", request.ExtractionProfile);
        Assert.Equal("m.json", request.MetadataOutputPath);
        Assert.Equal("r.json", request.MetricsOutputPath);
        Assert.Equal(1, factory.Created);
        Assert.Equal(1, session.GetOrStartCalls);
        Assert.NotNull(service.Progress);
    }

    // ------------------------------------------------------------ helpers

    private string CreateScratchFile()
    {
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, "model.edb");
        File.WriteAllText(file, "x");
        return file;
    }

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private JsonElement ValidPayload(string file) => JsonSerializer.SerializeToElement(new
    {
        filePath = file,
        outputDir = _directory,
        expectedDocument = new { path = file, fileId = "id" },
        expectedUnits = new { force = "kip", length = "ft" },
        unitPolicy = "checkOnly",
        extractionProfile = "full"
    });

    private OperationManager CreateManager(FakeSession session, RecordingService service, CountingFactory factory)
    {
        var definition = new AnalyzeVersionOperation(
            session, service, factory, new FakeStatusService(), new CachedSessionStatus());
        _manager = new OperationManager(
            new StaExecutionWorker(),
            new OperationEventJournalFactory(_directory),
            new SystemOperationClock(),
            WorkEnvelopeFixtures.Consented(session),
            [definition]);
        return _manager;
    }

    private sealed class RecordingService : IAnalyzeVersionService
    {
        public int Calls { get; private set; }
        public AnalyzeVersionRequest? Request { get; private set; }
        public IEtabsOperationProgress? Progress { get; private set; }

        public Task<Result<AnalyzeVersionData>> AnalyzeOnApiAsync(
            IAnalyzeVersionApi api, AnalyzeVersionRequest request, IEtabsOperationProgress? progress = null)
        {
            Calls++;
            Request = request;
            Progress = progress;
            var block = CheckedUnits.FromRaw(2, 2, 1, out _)!;
            return Task.FromResult(Result.Ok(new AnalyzeVersionData
            {
                Units = new AnalyzeVersionUnits
                {
                    Database = block,
                    Expected = block,
                    PresentBefore = block,
                    Tables = block,
                    PresentAfter = block,
                    Policy = "checkOnly"
                }
            }));
        }
    }

    private sealed class CountingFactory : IAnalyzeVersionApiFactory
    {
        public int Created { get; private set; }

        public IAnalyzeVersionApi Create(ETABSApplication application)
        {
            Created++;
            return null!;
        }
    }

    private sealed class FakeStatusService : IGetStatusService
    {
        public Task<Result<GetStatusData>> GetStatusAsync() => throw new NotSupportedException();
        public Result<GetStatusData> GetStatusOnApp(ETABSApplication app, int? pid = null) =>
            Result.Ok(new GetStatusData { IsRunning = true, Pid = pid });
    }

    private sealed class FakeSession(bool isStarted = true) : IEtabsSession
    {
        public Result CertifyNoUnconsentedExposure() => Result.Ok();
        public void MarkVisibilityStage(string stage) { }
        public int GetOrStartCalls { get; private set; }
        public bool IsStarted => isStarted;
        public bool IsUserVisible { get; init; }
        public int? ProcessId => 42;
        public ETABSApplication GetOrStart()
        {
            GetOrStartCalls++;
            return null!;
        }
        public IManagedEtabsApplication GetOrStartOwned() => throw new NotSupportedException();
        public Result RevealForExplicitUserRequest() => Result.Ok();
        public ManagedEtabsShutdownResult Shutdown() => throw new NotSupportedException();
        public ManagedEtabsShutdownResult ShutdownWhileWorkerBlocked() => throw new NotSupportedException();
        public void Dispose() { }
    }
}
