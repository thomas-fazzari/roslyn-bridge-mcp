// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;

namespace RoslynBridge.Features.Edit.Proposals;

/// <summary>
/// Previews or applies a pending proposal again. The proposal already knows its target.
/// </summary>
/// <param name="ActionIndex">Picks a code action from a listing.</param>
internal sealed record ProposalRequest(
    LspAction Action,
    string ProposalId,
    int? ActionIndex,
    bool Apply,
    Paging Page
) : EditRequest(Apply, Page)
{
    public override LspAction Action { get; } = Action;

    protected override ServerPreparation Server => ServerPreparation.StartAndSynchronize;
}
