// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using RoslynBridge.Changes;
using RoslynBridge.Editing;
using RoslynBridge.Lsp;
using RoslynBridge.Queries;
using RoslynBridge.Requests;
using RoslynBridge.Settings;
using RoslynBridge.Symbols;
using RoslynBridge.Tools;

namespace RoslynBridge.Tests.Unit;

/// <summary>
/// Calls the tool that owns a request's action, around one <see cref="LspTool"/>.
/// </summary>
internal sealed class ToolClient : IDisposable
{
    private readonly SettingsMonitor _settings;

    public ToolClient(string root, RoslynSession session, SettingsMonitor settings)
    {
        _settings = settings;
        var paths = new WorkspacePaths(root);
        var queries = new LspQueries(session, paths, new SymbolResolver(session, paths));
        Lsp = new LspTool(
            settings,
            paths,
            session,
            queries,
            new LspChanges(session, paths, new WorkspaceEditService(paths), queries),
            NullLogger<LspTool>.Instance
        );
    }

    public LspTool Lsp { get; }

    public Task<CallToolResult> ExecuteAsync(
        LspRequest request,
        CancellationToken cancellationToken
    ) =>
        request.Action switch
        {
            LspAction.Diagnostics => new DiagnosticsTool(Lsp, _settings).DiagnosticsAsync(
                request.File,
                request.Severity,
                request.Ids is null ? null : [.. request.Ids],
                request.Summary,
                request.Limit,
                request.Offset,
                cancellationToken
            ),
            LspAction.Symbols => new SymbolsTool(Lsp).SymbolsAsync(
                request.File,
                request.Query,
                request.Unused,
                request.Limit,
                request.Offset,
                cancellationToken
            ),
            LspAction.Bulk => new BulkNavigateTool(Lsp).BulkNavigateAsync(
                [.. request.Queries.Select(Query)],
                request.Context,
                request.Limit,
                request.Offset,
                cancellationToken
            ),
            _ when request.IsNavigation => new NavigateTool(Lsp).NavigateAsync(
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
            _ when request.IsEdit => new EditTool(Lsp).EditAsync(
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
            _ => new ServerTool(Lsp).ServerAsync(
                Enum.Parse<ServerAction>(request.Action.ToString()),
                request.Method,
                request.Parameters,
                request.Apply,
                cancellationToken
            ),
        };

    public void Dispose() => Lsp.Dispose();

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
