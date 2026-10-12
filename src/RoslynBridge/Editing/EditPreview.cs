// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;

namespace RoslynBridge.Editing;

internal static class EditPreview
{
    /// <summary>
    /// Drops trailing files, then the command, until the preview fits in <paramref name="budget"/> characters.
    /// </summary>
    public static JsonObject Limit(JsonObject preview, int budget)
    {
        var files = preview["files"]!.AsArray();
        preview.Remove("files");
        var included = new JsonArray();
        preview["files"] = included;
        preview["filesTruncated"] = false;
        var remaining = budget - Length(preview);
        if (remaining < 0 && preview["command"] is not null)
        {
            preview.Remove("command");
            preview["commandPreviewTruncated"] = true;
            preview["previewTruncated"] = true;
            remaining = budget - Length(preview);
        }

        if (!KeepLeadingFiles(files, included, remaining))
        {
            preview["filesTruncated"] = true;
            preview["previewTruncated"] = true;
        }

        return preview;
    }

    /// <summary>
    /// Moves the leading <paramref name="files"/> that fit in <paramref name="remaining"/> characters
    /// into <paramref name="included"/>.
    /// </summary>
    /// <returns>Whether every file fit.</returns>
    internal static bool KeepLeadingFiles(JsonArray files, JsonArray included, int remaining)
    {
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index]!;
            var length = Length(file) + (included.Count == 0 ? 0 : 1);
            if (length > remaining)
            {
                return false;
            }

            // Detaches the file so another array can own it without a copy
            files[index] = null;
            included.Add(file);
            remaining -= length;
        }

        return true;
    }

    private static int Length(JsonNode node) =>
        node.ToJsonString(BridgeJsonContext.Default.Options).Length;
}
