// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;

namespace RoslynBridge.Requests;

/// <summary>
/// One operation dispatched by a tool to the shared Roslyn session.
/// </summary>
internal sealed record LspRequest
{
    internal const int DefaultResultLimit = 50;
    internal const int MaximumResultLimit = 250;
    internal const int MaximumBulkQueries = 16;

    public required LspAction Action { get; init; }

    public string? File { get; init; }

    public string? Symbol { get; init; }

    public int? Line { get; init; }

    public int? Character { get; init; }

    public int? EndLine { get; init; }

    public int? EndCharacter { get; init; }

    public string? Query { get; init; }

    public string? NewName { get; init; }

    public string? Diagnostic { get; init; }

    public int? ActionIndex { get; init; }

    public bool Apply { get; init; }

    public bool All { get; init; }

    public bool Context { get; init; }

    public bool Inherited { get; init; }

    public string? ProposalId { get; init; }

    public string? Method { get; init; }

    public JsonObject? Parameters { get; init; }

    /// <summary>
    /// The independent navigation requests of a bulk request. They take their limit, offset and context from it.
    /// </summary>
    public IReadOnlyList<LspRequest> Queries { get; init; } = [];

    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Warning;

    public IReadOnlyList<string>? Ids { get; init; }

    public bool Summary { get; init; }

    public int Limit { get; init; } = DefaultResultLimit;

    public int Offset { get; init; }

    /// <summary>
    /// Returns the offset of the page after the one at <paramref name="offset"/>, or null on the last page.
    /// </summary>
    internal static int? NextOffset(int offset, int limit, int total) =>
        offset < total - limit ? offset + limit : null;
}
