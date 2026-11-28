// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Changes;
using RoslynBridge.Common;
using RoslynBridge.Lsp;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Results;

namespace RoslynBridge.Features.Server.Raw;

internal sealed class RawRequestHandler(
    RoslynSession session,
    WorkspacePaths paths,
    ChangeExecutor executor
) : IToolHandler<RawRequest, ToolResult>
{
    public async Task<Result<ToolResult>> HandleAsync(
        RawRequest request,
        CancellationToken cancellationToken
    )
    {
        if (await OpenDocumentAsync(request.Parameters, cancellationToken) is ToolError error)
            return error;

        if (request.Method is LspMethods.WORKSPACE_EXECUTE_COMMAND)
        {
            var command = new LspCommand(request.Parameters ?? []);
            return (await executor.ExecuteCommandAsync(command, cancellationToken)).AsToolResult();
        }

        var answer = await session.RequestAsync(
            request.Method,
            request.Parameters,
            cancellationToken
        );
        return answer.Map<ToolResult>(json => new JsonResult(json));
    }

    // Roslyn answers a request on a C# document only once the document is open
    private async Task<Result> OpenDocumentAsync(
        JsonObject? parameters,
        CancellationToken cancellationToken
    )
    {
        if (
            parameters?["textDocument"]?["uri"] is not JsonValue uri
            || !uri.TryGetValue<string>(out var documentUri)
            || !WorkspacePaths.IsCSharpFile(documentUri)
        )
        {
            return Result.Success;
        }

        if (paths.Resolve(documentUri).IsError(out var pathError, out var file))
            return pathError;

        var opened = await session.OpenDocumentAsync(file, cancellationToken);
        return opened.IsError(out var error, out _) ? error : Result.Success;
    }
}
