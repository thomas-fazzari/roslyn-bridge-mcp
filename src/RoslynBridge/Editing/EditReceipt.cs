// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;

namespace RoslynBridge.Editing;

internal static class EditReceipt
{
    /// <summary>
    /// Builds an application receipt from completed file operations.
    /// The receipt reports <c>synchronized</c> as false until the caller refreshes Roslyn.
    /// </summary>
    /// <param name="files">Operations without a parent node, in completion order.</param>
    public static JsonObject Create(bool applied, IEnumerable<JsonNode> files)
    {
        var operations = new JsonArray();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            operations.Add(file);
            paths.Add(file["path"]!.GetValue<string>());
        }

        return new JsonObject
        {
            ["applied"] = applied,
            ["partial"] = !applied && paths.Count != 0,
            ["synchronized"] = false,
            ["fileCount"] = paths.Count,
            ["operationCount"] = operations.Count,
            ["files"] = operations,
        };
    }

    public static bool IsFileMembershipChange(JsonNode file) =>
        file["kind"]?.GetValue<string>()
            is WorkspaceEditService.CreateFileOperation
                or WorkspaceEditService.DeleteFileOperation
                or WorkspaceEditService.RenameFileOperation;
}
