// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;

namespace RoslynBridge.Changes;

/// <summary>
/// Pending proposals of the current Roslyn process. A restart, a crash included, makes them unknown.
/// </summary>
[DebuggerDisplay("{_pending.Count} pending proposals")]
internal sealed class ProposalStore(RoslynSession session)
{
    internal const int MaximumPendingProposals = 8;
    private static readonly TimeSpan _proposalLifetime = TimeSpan.FromMinutes(5);

    // Insertion order is the creation order, so eviction and expiry start at index 0
    private readonly OrderedDictionary<string, Proposal> _pending = [with(StringComparer.Ordinal)];

    public Result<PendingChange> Find(string proposalId, LspAction action) =>
        _pending.TryGetValue(proposalId, out var proposal)
        && proposal.Change.Action == action
        && proposal.Generation == session.Generation
        && !IsExpired(proposal)
            ? proposal.Change
            : ToolError.InvalidRequest(
                "The proposal is unknown or expired. Request a new preview."
            );

    public string Remember(PendingChange change)
    {
        while (
            _pending.Count != 0
            && (_pending.Count >= MaximumPendingProposals || IsExpired(_pending.GetAt(0).Value))
        )
        {
            _pending.RemoveAt(0);
        }

        var id = Guid.NewGuid().ToString("N");
        _pending.Add(id, new Proposal(change, session.Generation));
        return id;
    }

    public void Remove(string proposalId) => _pending.Remove(proposalId);

    private static bool IsExpired(Proposal proposal) =>
        DateTimeOffset.UtcNow - proposal.Change.CreatedAt > _proposalLifetime;

    private sealed record Proposal(PendingChange Change, int Generation);
}
