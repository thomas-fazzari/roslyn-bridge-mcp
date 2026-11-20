// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using static RoslynBridge.Results.ResultText;

namespace RoslynBridge.Results;

/// <summary>
/// Paths are workspace-relative. OldPath is set for a rename.
/// </summary>
internal sealed record FileOperation(string Path, FileChange Kind, string? OldPath = null);

internal enum FileChange
{
    Change = 0,
    Create = 1,
    Rename = 2,
    Delete = 3,
}

internal static class FileChanges
{
    extension(FileChange change)
    {
        public string Name => change.ToString().ToLowerInvariant();
    }
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

/// <param name="Result">What Roslyn returned for the command, if anything.</param>
internal sealed record CommandRun(bool Executed, JsonNode? Result = null);

/// <summary>
/// Synchronized stays false until Roslyn is refreshed after the writes.
/// </summary>
internal sealed record EditReceipt(bool Applied, IReadOnlyList<FileOperation> Operations)
    : ToolResult
{
    public bool Synchronized { get; init; }

    /// <summary>
    /// Null when the edit has no command.
    /// </summary>
    public CommandRun? Command { get; init; }

    public DiagnosticReport? Diagnostics { get; init; }

    public bool Partial => !Applied && Operations.Count > 0;

    public int FileCount =>
        Operations.Select(operation => operation.Path).Distinct(StringComparer.Ordinal).Count();

    // The client must learn which files changed, so the receipt drops trailing operations instead of failing
    public override bool MustDeliver => true;

    public EditReceipt Merge(EditReceipt next) =>
        new(Applied && next.Applied, [.. Operations, .. next.Operations])
        {
            Command = next.Command ?? Command,
        };

    internal override void Write(StringBuilder text, int budget)
    {
        var status = (Applied, Partial) switch
        {
            (true, _) => "applied",
            (_, true) => "partially applied",
            _ => "not applied",
        };
        text.Append(status)
            .Append(": ")
            .Append(Count(FileCount, "file", "files"))
            .Append(", ")
            .Append(Count(Operations.Count, "operation", "operations"))
            .Append(Synchronized ? ", Roslyn synchronized\n" : ", Roslyn not synchronized\n");

        var shown = AppendWithin(text, Operations.Select(OperationLine), budget);
        if (shown < Operations.Count)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"truncated: {Operations.Count - shown} more operations not shown\n"
            );
        }

        if (Command is { } command)
        {
            text.Append(command.Executed ? "command executed" : "command not executed");
            if (command.Result is { } value)
            {
                var commandResult = Json(value);
                text.Append(
                    text.Length + commandResult.Length + 10 <= budget
                        ? $", result {commandResult}"
                        : ", result omitted: too large to show"
                );
            }

            text.Append('\n');
        }

        Diagnostics?.Write(text, budget);
    }
}

/// <param name="Command">The command a code action runs when applied.</param>
internal sealed record ProposalPreview(
    string ProposalId,
    IReadOnlyList<FilePreview> Files,
    JsonNode? Command
) : ToolResult
{
    public bool Truncated => Files.Any(file => file.Truncated);

    internal override void Write(StringBuilder text, int budget)
    {
        text.Append("proposal ")
            .Append(ProposalId)
            .Append(": ")
            .Append(Count(Files.Count, "file", "files"))
            .Append(", not applied\n");
        var shown = AppendWithin(text, Files.Select(FileBlock), budget);
        if (
            Command is { } command
            && AppendWithin(text, [$"command {Json(command)}\n"], budget) == 0
        )
        {
            text.Append("command omitted: too large to show\n");
        }

        if (shown < Files.Count)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"truncated: {shown} of {Files.Count} files shown\n"
            );
        }

        if (Truncated)
            text.Append("preview truncated: some changes are not shown in full\n");
    }

    private static string FileBlock(FilePreview file)
    {
        var text = new StringBuilder(OperationLine(file.Operation));
        foreach (var change in file.Changes)
        {
            var from = Position(change.BeforeStart);
            var to = Position(change.AfterStart);
            text.Append("@@ ").Append(from);
            if (!string.Equals(from, to, StringComparison.Ordinal))
                text.Append(" -> ").Append(to);

            text.Append('\n');
            AppendHunkLines(text, '-', change.Before);
            AppendHunkLines(text, '+', change.After);
        }

        return text.ToString();
    }

    private static void AppendHunkLines(StringBuilder text, char prefix, string excerpt)
    {
        if (excerpt.Length == 0)
            return;

        foreach (var line in excerpt.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'))
        {
            text.Append(prefix).Append(' ').Append(line).Append('\n');
        }
    }
}
