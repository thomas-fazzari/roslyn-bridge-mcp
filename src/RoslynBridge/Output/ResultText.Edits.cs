// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace RoslynBridge.Output;

internal static partial class ResultText
{
    // Room kept for the closing lines that report what was left out
    private const int ClosingReserve = 200;

    // Applied changes render as receipts before reaching here
    private static void AppendChange(StringBuilder text, JsonObject result, int budget)
    {
        if (result["actions"] is JsonArray actions)
        {
            AppendActions(text, result, actions);
        }
        else
        {
            AppendPreview(text, result, budget);
        }
    }

    private static void AppendActions(StringBuilder text, JsonObject result, JsonArray actions)
    {
        text.Append("proposal ").Append(result["proposalId"]!.GetValue<string>()).Append('\n');
        foreach (var action in actions.OfType<JsonObject>())
        {
            text.Append(Int(action, "index").ToString(CultureInfo.InvariantCulture));
            if (action["kind"]?.GetValue<string>() is { } kind)
            {
                text.Append(' ').Append(kind);
            }

            text.Append(' ')
                .Append(OneLine(action["title"]?.GetValue<string>() ?? string.Empty))
                .Append('\n');
        }

        AppendEmptyOrTruncation(text, result, "actions");
    }

    private static void AppendPreview(StringBuilder text, JsonObject result, int budget)
    {
        var files = Items(result, "files").ToList();
        text.Append("proposal ")
            .Append(result["proposalId"]!.GetValue<string>())
            .Append(": ")
            .Append(Count(files.Count, "file", "files"))
            .Append(", not applied\n");
        var shown = AppendWithin(text, files.Select(PreviewOfFile), budget);
        if (
            result["command"] is { } command
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

        if (result["previewTruncated"]?.GetValue<bool>() is true)
        {
            text.Append("preview truncated: some changes are not shown in full\n");
        }
    }

    private static string PreviewOfFile(JsonObject file)
    {
        var text = new StringBuilder(Operation(file));
        foreach (var change in (file["changes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            AppendHunk(text, change);
        }

        return text.ToString();
    }

    private static void AppendHunk(StringBuilder text, JsonObject change)
    {
        var before = change["before"]!;
        var after = change["after"]!;
        var from = Position(before["startLine"]!, before["startCharacter"]!);
        var to = Position(after["startLine"]!, after["startCharacter"]!);
        text.Append("@@ ").Append(from);
        if (!string.Equals(from, to, StringComparison.Ordinal))
        {
            text.Append(" -> ").Append(to);
        }

        text.Append('\n');
        AppendHunkLines(text, '-', before["text"]!.GetValue<string>());
        AppendHunkLines(text, '+', after["text"]!.GetValue<string>());
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

    private static void AppendReceipt(StringBuilder text, JsonObject result, int budget)
    {
        var status = (
            result["applied"]?.GetValue<bool>(),
            result["partial"]?.GetValue<bool>()
        ) switch
        {
            (true, _) => "applied",
            (_, true) => "partially applied",
            _ => "not applied",
        };
        text.Append(status)
            .Append(": ")
            .Append(Count(Int(result, "fileCount"), "file", "files"))
            .Append(", ")
            .Append(Count(Int(result, "operationCount"), "operation", "operations"))
            .Append(
                result["synchronized"]?.GetValue<bool>() is true
                    ? ", Roslyn synchronized\n"
                    : ", Roslyn not synchronized\n"
            );

        var files = Items(result, "files").ToList();
        var shown = AppendWithin(text, files.Select(Operation), budget);
        if (shown < files.Count)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"truncated: {files.Count - shown} more operations not shown\n"
            );
        }

        if (result["commandExecuted"]?.GetValue<bool>() is { } executed)
        {
            text.Append(executed ? "command executed" : "command not executed");
            var commandResult = result["commandResult"] is { } value ? Json(value) : null;
            if (commandResult is not null && text.Length + commandResult.Length + 10 <= budget)
            {
                text.Append(", result ").Append(commandResult);
            }
            else if (commandResult is not null)
            {
                text.Append(", result omitted: too large to show");
            }

            text.Append('\n');
        }
    }

    // kind path, or rename old -> new
    private static string Operation(JsonObject file)
    {
        var path = file["path"]!.GetValue<string>();
        var kind = file["kind"]!.GetValue<string>();
        return file["oldPath"]?.GetValue<string>() is { } oldPath
            ? $"{kind} {oldPath} -> {path}\n"
            : $"{kind} {path}\n";
    }

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
