// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Changes;
using RoslynBridge.Common;
using RoslynBridge.Lsp;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Results;
using RoslynBridge.Symbols;

namespace RoslynBridge.Features.Edit.Rename;

internal sealed class RenameHandler(
    RoslynSession session,
    SymbolLocator locator,
    ChangeExecutor executor
) : IToolHandler<RenameRequest, ToolResult>
{
    public async Task<Result<ToolResult>> HandleAsync(
        RenameRequest request,
        CancellationToken cancellationToken
    )
    {
        var captured = await executor.CaptureAsync(
            token => locator.PositionAsync(request.Target, SymbolLookup.AtPosition, token),
            cancellationToken
        );
        if (captured.IsError(out var error, out var located))
            return error;

        var (versioned, position) = located;
        var renamed = await session.RenameAsync(position, request.NewName, cancellationToken);
        if (renamed.IsError(out var renameError, out var edit))
            return renameError;

        return edit is null
            ? await RenameRefusalAsync(position, request.NewName, cancellationToken)
            : await executor.CompleteAsync(
                new ProposedEdit(request.Action, versioned, edit),
                request.Apply,
                cancellationToken
            );
    }

    // Roslyn returns no edit both for a position without a symbol and for a name it rejects
    private async Task<ToolError> RenameRefusalAsync(
        DocumentPosition position,
        string newName,
        CancellationToken cancellationToken
    )
    {
        var preparation = await session.PrepareRenameAsync(position, cancellationToken);
        if (preparation.IsError(out var error, out var prepared))
            return error;

        return prepared
            ? ToolError.InvalidRequest(
                $"Roslyn rejects '{newName}' as the new name. Pass one valid C# identifier, without dots."
            )
            : await locator.MissingSymbolAsync(position, cancellationToken)
                ?? ToolError.InvalidRequest(
                    "Roslyn cannot rename this symbol. It may be declared outside the workspace."
                );
    }
}
