// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabSharp.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;

/// <summary>
/// Creates table service instances bound to a specific ETABSApplication.
///
/// WHY A FACTORY AND NOT DIRECT DI REGISTRATION:
///   EtabsTableQueryService and EtabsTableEditingService both require a live
///   ETABSApplication, which is created per-command (Mode A: Connect, Mode B:
///   CreateNew) and disposed in the finally block.  It cannot be a DI singleton
///   or scoped service because its lifetime is controlled by the command, not
///   the container.  The factory is registered as a singleton; each command
///   calls factory.Create(app) after it has opened the model and discards the
///   services when it disposes the app.
/// </summary>
public interface IEtabsTableServicesFactory
{
    IEtabsTableQueryService CreateQueryService(ETABSApplication app);
    IEtabsTableEditingService CreateEditingService(ETABSApplication app);
}

public class EtabsTableServicesFactory : IEtabsTableServicesFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public EtabsTableServicesFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IEtabsTableQueryService CreateQueryService(ETABSApplication app) =>
        new EtabsTableQueryService(app, _loggerFactory.CreateLogger<EtabsTableQueryService>());

    public IEtabsTableEditingService CreateEditingService(ETABSApplication app) =>
        new EtabsTableEditingService(app, _loggerFactory.CreateLogger<EtabsTableEditingService>());
}

public static class EtabsTableServicesExtensions
{
    /// <summary>
    /// Registers the table services factory.
    /// Called from AddEtabsInfrastructure — no need to call this directly.
    /// </summary>
    public static IServiceCollection AddEtabsTableServices(this IServiceCollection services)
    {
        services.AddSingleton<IEtabsTableServicesFactory, EtabsTableServicesFactory>();
        return services;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// HOW TO USE IN A FEATURE SERVICE
// ─────────────────────────────────────────────────────────────────────────────
//
// 1. Inject IEtabsTableServicesFactory via constructor.
// 2. After ETABSWrapper.Connect() / CreateNew(), call factory.Create*(app).
// 3. Use the services. Dispose app in finally as usual.
//
// public class ExtractResultsService : IExtractResultsService
// {
//     private readonly IEtabsTableServicesFactory _tableFactory;
//
//     public ExtractResultsService(IEtabsTableServicesFactory tableFactory)
//     {
//         _tableFactory = tableFactory;
//     }
//
//     public async Task<Result<ExtractResultsData>> ExtractAsync(string filePath, ...)
//     {
//         ETABSApplication? app = null;
//         try
//         {
//             app = ETABSWrapper.CreateNew();
//             app.Application.Hide();
//             app.Model.Files.OpenFile(filePath);
//
//             var query   = _tableFactory.CreateQueryService(app);
//             var editing = _tableFactory.CreateEditingService(app);
//
//             // query and edit as needed ...
//         }
//         finally
//         {
//             app?.Application.ApplicationExit(false);
//             app?.Dispose();
//         }
//     }
// }
