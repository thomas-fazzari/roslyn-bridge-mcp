// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;

namespace RoslynBridge.Changes;

/// <summary>
/// Pending proposals of the current Roslyn process. A restart, a crash included, makes them unknown.
/// </summary>
[DebuggerDisplay("{_pending.Count} pending proposals")]
internal sealed class ProposalStore(RoslynSession session, TimeProvider time)
{
    internal const int MAXIMUM_PENDING_PROPOSALS = 8;
    internal static readonly TimeSpan ProposalLifetime = TimeSpan.FromMinutes(5);

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
            && (_pending.Count >= MAXIMUM_PENDING_PROPOSALS || IsExpired(_pending.GetAt(0).Value))
        )
        {
            _pending.RemoveAt(0);
        }

        var id = Guid.NewGuid().ToString("N");
        _pending.Add(id, new Proposal(change, session.Generation, time.GetUtcNow()));
        return id;
    }

    public void Remove(string proposalId) => _pending.Remove(proposalId);

    private bool IsExpired(Proposal proposal) =>
        time.GetUtcNow() - proposal.Created > ProposalLifetime;

    private sealed record Proposal(PendingChange Change, int Generation, DateTimeOffset Created);
}
