// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp.Protocol;

/// <summary>
/// Reads the LSP answers whose shape varies, so that no record describes them alone.
/// </summary>
internal static class LspAnswers
{
    // VS Code implements these commands itself, so the server has no handler for them
    private const string CLIENT_COMMAND_PREFIX = "roslyn.client.";

    // A location result is null, one location or an array of them
    public static Result<LspLocation[]> ReadLocations(JsonNode? answer) =>
        answer switch
        {
            null => Array.Empty<LspLocation>(),
            JsonArray => answer.Read<LspLocation[]>(),
            JsonObject => answer.Read<LspLocation>().Map(location => new[] { location }),
            _ => ToolError.InvalidLspData("Roslyn returned an invalid location result."),
        };

    public static Result<HierarchyItem[]> ReadHierarchy(JsonNode? answer) =>
        (answer as JsonArray ?? [])
            .Select(item =>
                item.Read<LspSymbol>().Map(symbol => new HierarchyItem(symbol, item!.DeepClone()))
            )
            .All()
            .Map(items => items.ToArray());

    public static string ReadHover(JsonNode? answer) => HoverText(answer?["contents"]).Trim();

    public static Result<ResolvedCodeAction> ReadResolvedCodeAction(JsonNode? answer)
    {
        if (answer is not JsonObject value)
            return ToolError.InvalidLspData("Roslyn could not resolve the code action.");

        var command = value["command"] switch
        {
            JsonObject commandObject => commandObject,
            JsonValue => value,
            _ => null,
        };
        if (IsClientCommand(command))
            command = null;

        var rawEdit = value["edit"] as JsonObject;
        return rawEdit is null && command is null
            ? ToolError.InvalidLspData("The selected action contains no edit or command.")
            : WorkspaceEdit
                .Parse(rawEdit)
                .Map(edit => new ResolvedCodeAction(
                    edit,
                    command is null ? null : new LspCommand(command)
                ));
    }

    private static string HoverText(JsonNode? contents) =>
        contents switch
        {
            JsonArray parts => string.Join("\n\n", parts.Select(HoverText)),
            JsonObject markup => markup["value"].AsString() ?? string.Empty,
            _ => contents.AsString() ?? string.Empty,
        };

    private static bool IsClientCommand(JsonObject? command) =>
        command?["command"].AsString() is { } name
        && name.StartsWith(CLIENT_COMMAND_PREFIX, StringComparison.Ordinal);
}
