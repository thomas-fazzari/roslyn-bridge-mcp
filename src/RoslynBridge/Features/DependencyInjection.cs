// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;
using RoslynBridge.Common;
using RoslynBridge.Features.BulkNavigate;
using RoslynBridge.Features.Diagnostics;
using RoslynBridge.Features.Edit;
using RoslynBridge.Features.Navigate;
using RoslynBridge.Features.Server;
using RoslynBridge.Features.Symbols;

namespace RoslynBridge.Features;

internal static class FeatureServices
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddFeatures() =>
            services
                .AddNavigate()
                .AddBulkNavigate()
                .AddSymbols()
                .AddDiagnostics()
                .AddEdit()
                .AddServer();
    }

    extension(IMcpServerBuilder builder)
    {
        public IMcpServerBuilder WithFeatureTools() =>
            builder.WithTools([
                IRoslynTool.Create<NavigateTool>(),
                IRoslynTool.Create<BulkNavigateTool>(),
                IRoslynTool.Create<SymbolsTool>(),
                IRoslynTool.Create<DiagnosticsTool>(),
                IRoslynTool.Create<EditTool>(),
                IRoslynTool.Create<ServerTool>(),
            ]);
    }
}
