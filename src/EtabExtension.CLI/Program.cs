using System.CommandLine;
using EtabExtension.CLI.Features.CloseModel;
using EtabExtension.CLI.Features.ExtractMaterials;
//using EtabExtension.CLI.Features.ExtractResults;
using EtabExtension.CLI.Features.GenerateE2K;
using EtabExtension.CLI.Features.GetStatus;
using EtabExtension.CLI.Features.OpenModel;
using EtabExtension.CLI.Features.RunAnalysis;
using EtabExtension.CLI.Features.UnlockModel;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using Microsoft.Extensions.Hosting;

// Redirect ALL Console.WriteLine to stderr — only ExitWithResult() writes to stdout.
// Rust reads stdout for JSON; everything else is progress/debug noise on stderr.
Console.SetOut(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddEtabsInfrastructure()
    .AddGetStatusFeature()
    .AddOpenModelFeature()
    .AddCloseModelFeature()
    .AddUnlockModelFeature()
    .AddGenerateE2KFeature()
    .AddExtractMaterialsFeature()
    .AddRunAnalysisFeature();
//.AddExtractResultsFeature();
// TODO: ExtractResults is currently being build based on the new api

var app = builder.Build();

var rootCommand = new RootCommand("etab-cli — ETABS automation sidecar")
{
    Description = "Single-shot commands. Returns one JSON object on stdout. Progress on stderr."
};

rootCommand.Subcommands.Add(GetStatusCommand.Create(app.Services));
rootCommand.Subcommands.Add(OpenModelCommand.Create(app.Services));
rootCommand.Subcommands.Add(CloseModelCommand.Create(app.Services));
rootCommand.Subcommands.Add(UnlockModelCommand.Create(app.Services));
rootCommand.Subcommands.Add(GenerateE2KCommand.Create(app.Services));
rootCommand.Subcommands.Add(ExtractMaterialsCommand.Create(app.Services));
rootCommand.Subcommands.Add(RunAnalysisCommand.Create(app.Services));
//rootCommand.Subcommands.Add(ExtractResultsCommand.Create(app.Services));

return await rootCommand.Parse(args).InvokeAsync();
