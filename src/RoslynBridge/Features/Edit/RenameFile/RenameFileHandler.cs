// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Changes;
using RoslynBridge.Common;
using RoslynBridge.Lsp;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Results;

namespace RoslynBridge.Features.Edit.RenameFile;

internal sealed class RenameFileHandler(
    RoslynSession session,
    WorkspacePaths paths,
    ChangeExecutor executor
) : IToolHandler<RenameFileRequest, ToolResult>
{
    public async Task<Result<ToolResult>> HandleAsync(
        RenameFileRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!session.SupportsFileRename)
            return ToolError.InvalidRequest("Roslyn does not advertise file rename support.");

        if (paths.Resolve(request.File).IsError(out var oldError, out var oldFile))
            return oldError;

        if (paths.ResolveRenameTarget(request.NewName).IsError(out var newError, out var newFile))
            return newError;

        var oldUri = oldFile.ToUri();
        var newUri = newFile.ToUri();

        var captured = await executor.CaptureAsync(cancellationToken);
        if (captured.IsError(out var captureError, out var snapshot))
            return captureError;

        var renamed = await session.WillRenameFilesAsync(oldUri, newUri, cancellationToken);
        if (renamed.IsError(out var editError, out var willRename))
            return editError;

        var edit = new WorkspaceEdit([
            .. willRename.Operations,
            new Lsp.Protocol.RenameFile(oldUri, newUri, new FileOperationOptions()),
        ]);
        return await executor.CompleteAsync(
            new ProposedEdit(request.Action, snapshot, edit),
            request.Apply,
            cancellationToken
        );
    }
}
