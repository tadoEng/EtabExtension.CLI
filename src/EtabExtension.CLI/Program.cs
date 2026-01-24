using System.CommandLine;
using EtabExtension.CLI.Features.GenerateE2K;
using EtabExtension.CLI.Features.Validation;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using Microsoft.Extensions.Hosting;

// 1. Send ALL Console.WriteLine logs to stderr
Console.SetOut(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddEtabsInfrastructure();
builder.Services.AddValidationFeature();
builder.Services.AddGenerateE2KFeature();

var app = builder.Build();

var rootCommand = new RootCommand("EtabExtension.CLI - Etabs Automation CLI")
{
    Description = "CLI for ETABS automation. All commands return JSON on stdout."
};

rootCommand.Subcommands.Add(ValidateCommand.Create(app.Services));
rootCommand.Subcommands.Add(GenerateE2KCommand.Create(app.Services));

// 2. Just invoke normally — logs stay on stderr
return await rootCommand.Parse(args).InvokeAsync();
