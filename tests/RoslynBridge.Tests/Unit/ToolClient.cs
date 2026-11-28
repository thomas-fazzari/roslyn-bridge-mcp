// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;
using RoslynBridge.Features.BulkNavigate;
using RoslynBridge.Features.Diagnostics;
using RoslynBridge.Features.Edit;
using RoslynBridge.Features.Navigate;
using RoslynBridge.Features.Server;
using RoslynBridge.Features.Symbols;
using RoslynBridge.Lsp;
using RoslynBridge.Settings;

namespace RoslynBridge.Tests.Unit;

/// <summary>
/// Calls the tool that owns a request's action, with the services of the bridge around the given session.
/// </summary>
internal sealed class ToolClient(
    string root,
    RoslynSession session,
    SettingsMonitor settings,
    TimeProvider time
) : IDisposable
{
    private readonly ServiceProvider _services = Services(root, session, settings, time)
        .BuildServiceProvider();

    /// <summary>
    /// The services of the bridge, around a test session instead of a Roslyn process.
    /// </summary>
    public static IServiceCollection Services(
        string root,
        RoslynSession session,
        SettingsMonitor settings,
        TimeProvider time
    ) =>
        new ServiceCollection()
            .AddLogging()
            .AddRoslynBridge(settings)
            .Replace(ServiceDescriptor.Singleton(session))
            .Replace(ServiceDescriptor.Singleton(time))
            .Replace(ServiceDescriptor.Singleton(new WorkspacePaths(root)));

    public Task<CallToolResult> ExecuteAsync(
        LspRequest request,
        CancellationToken cancellationToken
    ) =>
        request.Action switch
        {
            LspAction.Diagnostics => Tool<DiagnosticsTool>()
                .DiagnosticsAsync(
                    request.File,
                    request.Severity,
                    request.Ids is null ? null : [.. request.Ids],
                    request.Summary,
                    request.Limit,
                    request.Offset,
                    cancellationToken
                ),
            LspAction.Symbols => Tool<SymbolsTool>()
                .SymbolsAsync(
                    request.File,
                    request.Query,
                    request.Unused,
                    request.Limit,
                    request.Offset,
                    cancellationToken
                ),
            LspAction.Bulk => Tool<BulkNavigateTool>()
                .BulkNavigateAsync(
                    [.. request.Queries.Select(Query)],
                    request.Context,
                    request.Limit,
                    request.Offset,
                    cancellationToken
                ),
            _ when request.IsNavigation => Tool<NavigateTool>()
                .NavigateAsync(
                    Enum.Parse<NavigationAction>(request.Action.ToString()),
                    request.Symbol,
                    request.File,
                    request.Line,
                    request.Character,
                    request.Context,
                    request.Inherited,
                    request.Limit,
                    request.Offset,
                    cancellationToken
                ),
            _ when request.IsEdit => Tool<EditTool>()
                .EditAsync(
                    Enum.Parse<EditAction>(request.Action.ToString()),
                    request.Symbol,
                    request.File,
                    request.Line,
                    request.Character,
                    request.EndLine,
                    request.EndCharacter,
                    request.NewName,
                    request.Diagnostic,
                    request.ActionIndex,
                    request.ProposalId,
                    request.Apply,
                    request.All,
                    request.Limit,
                    cancellationToken
                ),
            _ => Tool<ServerTool>()
                .ServerAsync(
                    Enum.Parse<ServerAction>(request.Action.ToString()),
                    request.Method,
                    request.Parameters,
                    request.Apply,
                    cancellationToken
                ),
        };

    public void Dispose() => _services.Dispose();

    // The MCP server creates a tool for each call the same way
    private T Tool<T>() => ActivatorUtilities.CreateInstance<T>(_services);

    private static BulkQuery Query(LspRequest query) =>
        new()
        {
            Action = Enum.Parse<NavigationAction>(query.Action.ToString()),
            Symbol = query.Symbol,
            File = query.File,
            Line = query.Line,
            Character = query.Character,
            Inherited = query.Inherited,
        };
}
