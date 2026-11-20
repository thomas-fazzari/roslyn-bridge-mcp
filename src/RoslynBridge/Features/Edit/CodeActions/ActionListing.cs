// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Results;
using static RoslynBridge.Results.ResultText;

namespace RoslynBridge.Features.Edit.CodeActions;

internal sealed record ListedAction(int Index, string? Kind, string Title);

/// <param name="Total">Before limiting, without hidden actions.</param>
/// <param name="Hidden">Suppress or configure actions left out.</param>
internal sealed record ActionListing(
    string ProposalId,
    IReadOnlyList<ListedAction> Actions,
    int Total,
    bool Truncated,
    int Hidden
) : ToolResult
{
    internal override void Write(StringBuilder text, int budget)
    {
        text.Append("proposal ").Append(ProposalId).Append('\n');
        foreach (var action in Actions)
        {
            text.Append(action.Index.ToString(CultureInfo.InvariantCulture));
            if (action.Kind is { } kind)
                text.Append(' ').Append(kind);

            text.Append(' ').Append(OneLine(action.Title)).Append('\n');
        }

        AppendEmptyOrTruncation(text, Total, "actions", nextOffset: null, limited: Truncated);
        if (Hidden > 0)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"{Count(Hidden, "suppress or configure action", "suppress or configure actions")} hidden. Set all to list them.\n"
            );
        }
    }
}
