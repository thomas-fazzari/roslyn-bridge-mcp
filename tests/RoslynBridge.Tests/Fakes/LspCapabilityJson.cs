// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Tests.Fakes;

internal static class LspCapabilityJson
{
    public static JsonObject WillRenameFiles() =>
        new()
        {
            ["workspace"] = new JsonObject
            {
                ["fileOperations"] = new JsonObject { ["willRename"] = new JsonObject() },
            },
        };
}
