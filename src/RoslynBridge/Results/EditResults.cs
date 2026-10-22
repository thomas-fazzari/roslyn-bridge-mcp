// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;

namespace RoslynBridge.Results;

/// <summary>
/// A file operation with workspace-relative paths. OldPath is set for a rename.
/// </summary>
internal sealed record FileOperation(string Path, string Kind, string? OldPath = null)
{
    // The LSP names of file operations, which the bridge reuses for its own
    public const string Change = "change";
    public const string Create = "create";
    public const string Rename = "rename";
    public const string Delete = "delete";
}

/// <summary>
/// Changed text before and after an edit, each from its one-based start, with some unchanged context.
/// </summary>
internal sealed record TextChange(
    SourcePosition BeforeStart,
    string Before,
    SourcePosition AfterStart,
    string After
);

/// <param name="Truncated">Whether some changes could not be shown in full.</param>
internal sealed record FilePreview(
    FileOperation Operation,
    IReadOnlyList<TextChange> Changes,
    bool Truncated
);

/// <summary>
/// The file operations an application completed, in order.
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

    /// <summary>
    /// Appends the operations of a later application, such as a command's edits.
    /// </summary>
    public EditReceipt Merge(EditReceipt next) =>
        new(Applied && next.Applied, [.. Operations, .. next.Operations])
        {
            CommandExecuted = next.CommandExecuted ?? CommandExecuted,
        };
}
