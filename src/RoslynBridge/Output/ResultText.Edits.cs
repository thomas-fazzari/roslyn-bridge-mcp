// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace RoslynBridge.Output;

internal static partial class ResultText
{
    private const int OmittedFilesReserve = 80;

    private static void AppendChange(StringBuilder text, JsonObject result)
    {
        if (result["actions"] is JsonArray actions)
        {
            AppendActions(text, result, actions);
        }
        else if (result["proposalId"] is not null)
        {
            AppendPreview(text, result);
        }
        else
        {
            AppendReceipt(text, result, int.MaxValue);
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

    private static void AppendPreview(StringBuilder text, JsonObject result)
    {
        var files = Items(result, "files").ToList();
        text.Append("proposal ")
            .Append(result["proposalId"]!.GetValue<string>())
            .Append(": ")
            .Append(Count(Int(result, "fileCount"), "file", "files"))
            .Append(", not applied\n");
        foreach (var file in files)
        {
            AppendOperation(text, file);
            foreach (var change in (file["changes"] as JsonArray ?? []).OfType<JsonObject>())
            {
                AppendHunk(text, change);
            }
        }

        if (result["command"] is { } command)
        {
            text.Append("command ").Append(Json(command)).Append('\n');
        }

        if (result["commandPreviewTruncated"]?.GetValue<bool>() is true)
        {
            text.Append("command omitted: too large to show\n");
        }

        if (result["filesTruncated"]?.GetValue<bool>() is true)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"truncated: {files.Count} of {Int(result, "fileCount")} files shown\n"
            );
        }

        if (result["previewTruncated"]?.GetValue<bool>() is true)
        {
            text.Append("preview truncated: some changes are not shown in full\n");
        }
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
        var shown = 0;
        foreach (var file in files)
        {
            var line = new StringBuilder();
            AppendOperation(line, file);
            if (text.Length + line.Length > budget - OmittedFilesReserve)
            {
                break;
            }

            text.Append(line);
            shown++;
        }

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

    private static void AppendOperation(StringBuilder text, JsonObject file)
    {
        text.Append(file["kind"]?.GetValue<string>() ?? "change").Append(' ');
        if (file["oldPath"]?.GetValue<string>() is { } oldPath)
        {
            text.Append(oldPath).Append(" -> ");
        }

        text.Append(file["path"]!.GetValue<string>()).Append('\n');
    }
}
