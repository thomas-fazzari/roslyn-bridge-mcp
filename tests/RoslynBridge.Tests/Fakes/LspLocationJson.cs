// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Tests.Fakes;

/// <summary>
/// Builds the LSP locations and ranges that fake servers return. Positions are zero-based.
/// </summary>
internal static class LspLocationJson
{
    public static JsonObject Location(string path, int line = 0, int character = 0) =>
        new() { ["uri"] = new Uri(path).AbsoluteUri, ["range"] = Range(line, character) };

    /// <returns>A range of one character from the position.</returns>
    public static JsonObject Range(int line, int character) =>
        new()
        {
            ["start"] = new JsonObject { ["line"] = line, ["character"] = character },
            ["end"] = new JsonObject { ["line"] = line, ["character"] = character + 1 },
        };
}
