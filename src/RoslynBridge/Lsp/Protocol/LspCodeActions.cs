// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp.Protocol;

/// <summary>
/// One entry of a code action listing. Roslyn expects <paramref name="Raw"/> back to resolve the action.
/// </summary>
/// <param name="Group">The first part of the code action path, which names the parent menu of a nested action.</param>
/// <param name="Scope">The fix-all scope, or null for an action that applies to one place.</param>
internal sealed record LspCodeAction(
    string Title,
    string? Kind,
    string? Group,
    JsonObject Raw,
    string? Scope
)
{
    /// <summary>
    /// Nested actions become one entry per child, and fix-all actions one entry per scope.
    /// </summary>
    internal static LspCodeAction[] ReadAll(JsonNode? response)
    {
        var actions = new List<LspCodeAction>();
        foreach (var action in (response as JsonArray ?? []).OfType<JsonObject>())
        {
            Add(action, actions);
        }

        return [.. actions];
    }

    private static void Add(JsonObject action, List<LspCodeAction> actions)
    {
        var data = action["data"] as JsonObject;
        if (data?["NestedCodeActions"] is JsonArray { Count: > 0 } nested)
        {
            foreach (var child in nested.OfType<JsonObject>())
            {
                Add(child, actions);
            }

            return;
        }

        var title = ActionTitle(action, data);
        var kind = action["kind"].AsString();
        var group = data?["CodeActionPath"] is JsonArray { Count: > 0 } path
            ? path[0].AsString()
            : null;
        if (data?["FixAllFlavors"] is JsonArray scopes)
        {
            foreach (var scope in scopes.Select(scope => scope.AsString()).OfType<string>())
            {
                actions.Add(new LspCodeAction($"{title} ({scope})", kind, group, action, scope));
            }

            return;
        }

        actions.Add(new LspCodeAction(title, kind, group, action, Scope: null));
    }

    // The path names the parent menus of a nested action, and its last part is the action itself
    private static string ActionTitle(JsonObject action, JsonObject? data)
    {
        var title = action["title"].AsString() ?? string.Empty;
        return data?["CodeActionPath"] is not JsonArray { Count: > 1 } path
            ? title
            : string.Join(
                " > ",
                path.Take(path.Count - 1)
                    .Select(part => part.AsString() ?? string.Empty)
                    .Append(title)
            );
    }
}

/// <param name="Command">Null when the action has none, or when the client has to run it.</param>
internal sealed record ResolvedCodeAction(WorkspaceEdit Edit, LspCommand? Command);
