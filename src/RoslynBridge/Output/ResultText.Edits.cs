// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using RoslynBridge.Changes;
using RoslynBridge.Editing;

namespace RoslynBridge.Output;

internal static partial class ResultText
{
    // Room kept for the closing lines that report what was left out
    private const int ClosingReserve = 200;

    private static void AppendActions(StringBuilder text, ActionListing listing)
    {
        text.Append("proposal ").Append(listing.ProposalId).Append('\n');
        foreach (var action in listing.Actions)
        {
            text.Append(action.Index.ToString(CultureInfo.InvariantCulture));
            if (action.Kind is { } kind)
            {
                text.Append(' ').Append(kind);
            }

            text.Append(' ').Append(OneLine(action.Title)).Append('\n');
        }

        AppendEmptyOrTruncation(text, listing.Total, listing.Truncated, "actions");
    }

    private static void AppendPreview(StringBuilder text, ProposalPreview preview, int budget)
    {
        var files = preview.Files;
        text.Append("proposal ")
            .Append(preview.ProposalId)
            .Append(": ")
            .Append(Count(files.Count, "file", "files"))
            .Append(", not applied\n");
        var shown = AppendWithin(text, files.Select(PreviewOfFile), budget);
        if (
            preview.Command is { } command
            && AppendWithin(text, [$"command {Json(command)}\n"], budget) == 0
        )
        {
            text.Append("command omitted: too large to show\n");
        }

        if (shown < files.Count)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"truncated: {shown} of {files.Count} files shown\n"
            );
        }

        if (preview.Truncated)
        {
            text.Append("preview truncated: some changes are not shown in full\n");
        }
    }

    private static string PreviewOfFile(FilePreview file)
    {
        var text = new StringBuilder(Operation(file.Operation));
        foreach (var change in file.Changes)
        {
            var from = Position(change.BeforeStart);
            var to = Position(change.AfterStart);
            text.Append("@@ ").Append(from);
            if (!string.Equals(from, to, StringComparison.Ordinal))
            {
                text.Append(" -> ").Append(to);
            }

            text.Append('\n');
            AppendHunkLines(text, '-', change.Before);
            AppendHunkLines(text, '+', change.After);
        }

        return text.ToString();
    }

    private static void AppendHunkLines(StringBuilder text, char prefix, string excerpt)
    {
        if (excerpt.Length == 0)
        {
            return;
        }

        foreach (var line in excerpt.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'))
        {
            text.Append(prefix).Append(' ').Append(line).Append('\n');
        }
    }

    private static void AppendReceipt(StringBuilder text, EditReceipt receipt, int budget)
    {
        var status = (receipt.Applied, receipt.Partial) switch
        {
            (true, _) => "applied",
            (_, true) => "partially applied",
            _ => "not applied",
        };
        text.Append(status)
            .Append(": ")
            .Append(Count(receipt.FileCount, "file", "files"))
            .Append(", ")
            .Append(Count(receipt.Operations.Count, "operation", "operations"))
            .Append(
                receipt.Synchronized ? ", Roslyn synchronized\n" : ", Roslyn not synchronized\n"
            );

        var shown = AppendWithin(text, receipt.Operations.Select(Operation), budget);
        if (shown < receipt.Operations.Count)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"truncated: {receipt.Operations.Count - shown} more operations not shown\n"
            );
        }

        if (receipt.CommandExecuted is not { } executed)
        {
            return;
        }

        text.Append(executed ? "command executed" : "command not executed");
        if (receipt.CommandResult is { } value)
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

    // kind path, or rename old -> new
    private static string Operation(FileOperation operation) =>
        operation.OldPath is { } oldPath
            ? $"{operation.Kind} {oldPath} -> {operation.Path}\n"
            : $"{operation.Kind} {operation.Path}\n";

    // Appends whole blocks while they fit, keeping room for the closing lines
    private static int AppendWithin(StringBuilder text, IEnumerable<string> blocks, int budget)
    {
        var shown = 0;
        foreach (var block in blocks)
        {
            if (text.Length + block.Length > budget - ClosingReserve)
            {
                break;
            }

            text.Append(block);
            shown++;
        }

        return shown;
    }
}
