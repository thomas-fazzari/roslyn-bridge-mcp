// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RoslynBridge.Changes;
using RoslynBridge.Editing;
using RoslynBridge.Lsp;
using RoslynBridge.Queries;
using RoslynBridge.Settings;
using RoslynBridge.Symbols;
using RoslynBridge.Telemetry;
using RoslynBridge.Tools;

namespace RoslynBridge;

internal static class BridgeServices
{
    extension(ILoggingBuilder logging)
    {
        /// <summary>
        /// Sends every log to standard error, which keeps standard output for MCP messages.
        /// </summary>
        public ILoggingBuilder AddStandardErrorConsole() =>
            logging.AddConsole(static options =>
                options.LogToStandardErrorThreshold = LogLevel.Trace
            );
    }

    extension(IServiceCollection services)
    {
        public IServiceCollection AddRoslynBridge(SettingsMonitor settings)
        {
            var options = settings.CurrentValue;
            var paths = new WorkspacePaths(options.WorkspaceRoot);
            paths.Exclude(options.Exclude);
            return services
                .AddSingleton(settings)
                .AddSingleton(paths)
                .AddSingleton<RoslynLauncher>(RoslynProcess.Start)
                .AddSingleton<RoslynSession>()
                .AddSingleton<WorkspaceEditService>()
                .AddSingleton<SymbolResolver>()
                .AddSingleton<LspQueries>()
                .AddSingleton<LspChanges>()
                .AddSingleton<LspTool>();
        }

        public IMcpServerBuilder AddRoslynMcpServer() =>
            services
                .AddMcpServer(static options =>
                    options.ServerInstructions = ToolDescriptions.ServerInstructions
                )
                .WithRoslynTools();
    }

    extension(IMcpServerBuilder builder)
    {
        public IMcpServerBuilder WithRoslynTools() =>
            builder
                .WithRequestFilters(static filters =>
                    filters
                        .AddCallToolFilter(ToolTelemetry.Measure)
                        .AddCallToolFilter(ToolArguments.Validate)
                )
                .WithToolsFromAssembly(typeof(BridgeServices).Assembly);
    }
}
