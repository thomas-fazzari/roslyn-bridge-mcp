// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RoslynBridge.Changes;
using RoslynBridge.Common;
using RoslynBridge.Editing;
using RoslynBridge.Features;
using RoslynBridge.Lsp;
using RoslynBridge.Settings;
using RoslynBridge.Symbols;
using RoslynBridge.Telemetry;
using RoslynBridge.Workspace;

namespace RoslynBridge;

internal static class BridgeServices
{
    // Claude Code keeps 2,048 characters of server instructions
    // Codex advises a self-contained first 512
    internal const string SERVER_INSTRUCTIONS =
        "Roslyn MCP: C# diagnostics, navigation and semantic edits from the official Roslyn language server, on saved files in the session's workspace. Prefer it over text search when symbol identity matters. diagnostics checks the files a task touches without a build, but does not replace builds or tests. diagnostics, navigate and symbols are read-only. edit returns a preview and a proposalId. Apply it with the same action, that proposalId and apply: true, and only when the user asked for the change.\n\nScope: request diagnostics for the files the task touches. Omitting file or passing a broad glob checks every C# file, so do that only for a requested workspace audit and split it by project. limit caps returned items, not analysis time. On loading, retry the same call. After a timeout, narrow the scope. If one file still times out, report it and stop retrying. A failed, incomplete or truncated result does not prove the workspace is clean.\n\nNavigation: prefer symbol over a position. Read a declaration with source, a type with members and a use's type and overload with expression, not whole files. Before changing a symbol, check its impact. On ambiguous_symbol or symbol_not_found, send back one returned candidate unchanged. Related symbols come back as names you can send again, for example to follow callers of callers. Batch independent navigations of known symbols with bulk_navigate. Set context: true to get source lines without reading files. Positions are one-based, except in raw lsp requests. A file may be a name or path suffix that matches one file.\n\nEdits: when the user already asked for the change, apply: true on the first call skips the preview, for a rename or for code_actions with an actionIndex. Otherwise, send the listing's proposalId and actionIndex to preview an action, then apply its new proposalId. To fix a diagnostic, pass its code as diagnostic with the file instead of a position. The apply result ends with the errors and warnings of the changed files. On stale_edit, request a fresh proposal.";

    extension(ILoggingBuilder logging)
    {
        /// <summary>
        /// Standard output is reserved for MCP messages.
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
                .AddSingleton(TimeProvider.System)
                .AddSingleton(paths)
                .AddSingleton<RoslynLauncher>(RoslynProcess.Start)
                .AddSingleton<RoslynSession>()
                .AddSingleton<WorkspaceEditService>()
                .AddSingleton<SymbolResolver>()
                .AddSingleton<SymbolLocator>()
                .AddSingleton<FileSelection>()
                .AddSingleton<DiagnosticScanner>()
                .AddFeatures()
                .AddSingleton<ProposalStore>()
                .AddSingleton<ChangeExecutor>()
                .AddSingleton<ToolPipeline>();
        }

        public IMcpServerBuilder AddRoslynMcpServer() =>
            services
                .AddMcpServer(static options => options.ServerInstructions = SERVER_INSTRUCTIONS)
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
                .WithFeatureTools();
    }
}
