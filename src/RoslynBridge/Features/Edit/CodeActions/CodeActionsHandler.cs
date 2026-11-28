// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Changes;
using RoslynBridge.Common;
using RoslynBridge.Lsp;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Results;
using RoslynBridge.Symbols;

namespace RoslynBridge.Features.Edit.CodeActions;

internal sealed class CodeActionsHandler(
    RoslynSession session,
    WorkspacePaths paths,
    SymbolLocator locator,
    ChangeExecutor executor,
    ProposalStore proposals
) : IToolHandler<CodeActionsRequest, ToolResult>
{
    internal const string SUPPRESSION_GROUP = "Suppress or configure issues";

    private const string QUICK_FIX_KIND = "quickfix";

    public async Task<Result<ToolResult>> HandleAsync(
        CodeActionsRequest request,
        CancellationToken cancellationToken
    )
    {
        var captured = await executor.CaptureAsync(
            token => RangeAsync(request.Scope, token),
            cancellationToken
        );
        if (captured.IsError(out var captureError, out var located))
            return captureError;

        var (versioned, range) = located;
        var listed = await session.CodeActionsAsync(range, cancellationToken);
        if (listed.IsError(out var requestError, out var actions))
            return requestError;

        var choices = actions.ToList();
        if (request.Scope is DiagnosticFixes)
        {
            choices.RemoveAll(choice =>
                !string.Equals(choice.Kind, QUICK_FIX_KIND, StringComparison.Ordinal)
            );
        }

        var pending = new ActionChoices(request.Action, versioned, choices);
        return request.ActionIndex is null
            ? Listing(proposals.Remember(pending), choices, request)
            : await executor.CompleteAsync(
                pending,
                request.ActionIndex,
                request.Apply,
                cancellationToken
            );
    }

    private static ActionListing Listing(
        string proposalId,
        List<LspCodeAction> choices,
        CodeActionsRequest request
    )
    {
        var shown = choices
            .Index()
            .Where(entry => request.All || !IsSuppression(entry.Item))
            .ToList();
        return new ActionListing(
            proposalId,
            [
                .. shown
                    .Take(request.Page.Limit)
                    .Select(entry => new ListedAction(
                        entry.Index,
                        entry.Item.Kind,
                        entry.Item.Title
                    )),
            ],
            shown.Count,
            shown.Count > request.Page.Limit,
            choices.Count - shown.Count
        );
    }

    // Roslyn marks the suppression group only by its title, which is English because the bridge requests the en locale
    private static bool IsSuppression(LspCodeAction action) =>
        string.Equals(action.Group, SUPPRESSION_GROUP, StringComparison.Ordinal);

    private async Task<Result<DocumentRange>> RangeAsync(
        CodeActionScope scope,
        CancellationToken cancellationToken
    ) =>
        scope switch
        {
            Selection selection => await SelectionAsync(selection, cancellationToken),
            DiagnosticFixes fixes => await DiagnosticRangeAsync(fixes, cancellationToken),
        };

    private async Task<Result<DocumentRange>> SelectionAsync(
        Selection selection,
        CancellationToken cancellationToken
    )
    {
        var position = await locator.PositionAsync(
            selection.Start,
            SymbolLookup.AtPosition,
            cancellationToken
        );
        if (position.IsError(out var error, out var start))
            return error;

        var end = selection.End is { } last ? LspPosition.From(last) : start.Position;
        return new DocumentRange(start.Uri, new LspRange(start.Position, end));
    }

    private async Task<Result<DocumentRange>> DiagnosticRangeAsync(
        DiagnosticFixes fixes,
        CancellationToken cancellationToken
    )
    {
        var (code, name, line) = fixes;
        if (paths.Resolve(name).IsError(out var pathError, out var file))
            return pathError;

        var opened = await session.OpenDocumentAsync(file, cancellationToken);
        if (opened.IsError(out var openError, out var uri))
            return openError;

        var reported = await session.DiagnosticReportAsync(uri, cancellationToken);
        if (reported.IsError(out var reportError, out var report))
            return reportError;

        var diagnostic = (report.Items ?? [])
            .Where(item =>
                string.Equals(item.CodeText(), code, StringComparison.OrdinalIgnoreCase)
                && (line is not { } wanted || item.Range.Start.OneBased.Line == wanted)
            )
            .MinBy(item => item.Range.Start);
        if (diagnostic is null)
        {
            var place = line is { } wanted
                ? string.Create(CultureInfo.InvariantCulture, $" on line {wanted}")
                : string.Empty;
            return ToolError.InvalidRequest(
                $"{file.RelativePath} has no {code} diagnostic{place}."
            );
        }

        return new DocumentRange(uri, diagnostic.Range);
    }
}
