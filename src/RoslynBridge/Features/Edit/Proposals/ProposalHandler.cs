// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Changes;
using RoslynBridge.Common;
using RoslynBridge.Results;

namespace RoslynBridge.Features.Edit.Proposals;

internal sealed class ProposalHandler(ProposalStore proposals, ChangeExecutor executor)
    : IToolHandler<ProposalRequest, ToolResult>
{
    public async Task<Result<ToolResult>> HandleAsync(
        ProposalRequest request,
        CancellationToken cancellationToken
    )
    {
        var found = proposals.Find(request.ProposalId, request.Action);
        if (found.IsError(out var error, out var pending))
            return error;

        var result = pending switch
        {
            ActionChoices choices => await executor.CompleteAsync(
                choices,
                request.ActionIndex,
                request.Apply,
                cancellationToken
            ),
            ProposedEdit edit => await executor.CompleteAsync(
                edit,
                request.Apply,
                cancellationToken
            ),
        };
        if (request.Apply && result is not ToolError)
            proposals.Remove(request.ProposalId);

        return result;
    }
}
