// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp.Protocol;

internal static class LspParameters
{
    public static JsonObject Document(string uri) =>
        new() { ["textDocument"] = new JsonObject { ["uri"] = uri } };

    public static JsonObject At(DocumentPosition target)
    {
        var parameters = Document(target.Uri);
        parameters["position"] = Position(target.Position);
        return parameters;
    }

    public static JsonObject In(DocumentRange target)
    {
        var parameters = Document(target.Uri);
        parameters["range"] = new JsonObject
        {
            ["start"] = Position(target.Range.Start),
            ["end"] = Position(target.Range.End),
        };
        return parameters;
    }

    public static JsonObject Item(HierarchyItem item) => new() { ["item"] = item.Raw.DeepClone() };

    private static JsonObject Position(LspPosition position) =>
        new() { ["line"] = position.Line, ["character"] = position.Character };
}
