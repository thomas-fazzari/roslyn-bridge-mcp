// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Results;

/// <summary>
/// Paths are workspace-relative. OldPath is set for a rename.
/// </summary>
internal sealed record FileOperation(string Path, string Kind, string? OldPath = null)
{
    public const string Change = "change";
    public const string Create = "create";
    public const string Rename = "rename";
    public const string Delete = "delete";
}

/// <summary>
/// Text before and after an edit from one-based starts, with some unchanged context.
/// </summary>
internal sealed record TextChange(
    SourcePosition BeforeStart,
    string Before,
    SourcePosition AfterStart,
    string After
);

internal sealed record FilePreview(
    FileOperation Operation,
    IReadOnlyList<TextChange> Changes,
    bool Truncated
);

/// <summary>
/// Synchronized stays false until Roslyn is refreshed after the writes.
/// </summary>
internal sealed record EditReceipt(bool Applied, IReadOnlyList<FileOperation> Operations)
    : ToolResult
{
    public bool Synchronized { get; init; }

    public bool? CommandExecuted { get; init; }

    public JsonNode? CommandResult { get; init; }

    public DiagnosticReport? Diagnostics { get; init; }

    public bool Partial => !Applied && Operations.Count > 0;

    public int FileCount =>
        Operations.Select(operation => operation.Path).Distinct(StringComparer.Ordinal).Count();

    public EditReceipt Merge(EditReceipt next) =>
        new(Applied && next.Applied, [.. Operations, .. next.Operations])
        {
            CommandExecuted = next.CommandExecuted ?? CommandExecuted,
        };
}
