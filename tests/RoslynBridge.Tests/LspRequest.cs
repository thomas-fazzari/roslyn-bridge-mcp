// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;
using RoslynBridge.Tools;

namespace RoslynBridge.Tests;

/// <summary>
/// The arguments of one tool call, with the action naming the tool that owns it.
/// </summary>
internal sealed record LspRequest
{
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

    public bool Unused { get; init; }

    public string? ProposalId { get; init; }

    public string? Method { get; init; }

    public JsonObject? Parameters { get; init; }

    public IReadOnlyList<LspRequest> Queries { get; init; } = [];

    /// <summary>
    /// Without it, the diagnostics tool uses the project setting.
    /// </summary>
    public DiagnosticSeverity? Severity { get; init; }

    public IReadOnlyList<string>? Ids { get; init; }

    public bool Summary { get; init; }

    public int Limit { get; init; } = Paging.DefaultLimit;

    public int Offset { get; init; }

    public bool IsNavigation => Enum.TryParse<NavigationAction>(Action.ToString(), out _);

    public bool IsEdit => Enum.TryParse<EditAction>(Action.ToString(), out _);
}
