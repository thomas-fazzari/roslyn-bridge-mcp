// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using RoslynBridge.Results;

namespace RoslynBridge.Changes;

internal sealed partial class LspChanges
{
    internal const string SuppressionGroup = "Suppress or configure issues";

    private const string QuickFixKind = "quickfix";

    // VS Code implements these commands itself, so the server has no handler for them
    private const string ClientCommandPrefix = "roslyn.client.";

    private async Task<Result<ToolResult>> CodeActionsAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await CaptureAsync(cancellationToken);
        var target = request.Diagnostic is null
            ? await SelectionAsync(request, cancellationToken)
            : await DiagnosticRangeAsync(request, cancellationToken);
        if (target.Error is { } error)
        {
            return error;
        }

        snapshot = await WithDocumentVersionsAsync(snapshot, cancellationToken);
        // Roslyn computes the diagnostics in the range itself, so the context stays empty
        var parameters = target.Value;
        parameters["context"] = new JsonObject { ["diagnostics"] = new JsonArray() };
        var response =
            await session.RequestAsync(
                LspMethods.TextDocumentCodeAction,
                parameters,
                cancellationToken
            ) as JsonArray
            ?? [];
        var choices = new List<CodeActionChoice>();
        foreach (var action in response.OfType<JsonObject>())
        {
            AddChoices(action, choices);
        }

        if (request.Diagnostic is not null)
        {
            choices.RemoveAll(choice =>
                !string.Equals(choice.Kind, QuickFixKind, StringComparison.Ordinal)
            );
        }

        var pending = new PendingChange(
            request.Action,
            snapshot,
            Edit: null,
            Command: null,
            choices
        );
        if (request.ActionIndex is not null)
        {
            return await SelectActionAsync(pending, request, cancellationToken);
        }

        if (request.Apply)
        {
            return ToolError.InvalidRequest("Choose actionIndex before applying a code action.");
        }

        return Listing(Remember(pending), choices, request);
    }

    private static ActionListing Listing(
        string proposalId,
        List<CodeActionChoice> choices,
        LspRequest request
    )
    {
        var shown = choices
            .Index()
            .Where(entry => request.All || !IsSuppression(entry.Item))
            .ToList();
        return new ActionListing(
            proposalId,
            [
                .. shown
                    .Take(request.Limit)
                    .Select(entry => new ListedAction(
                        entry.Index,
                        entry.Item.Kind,
                        entry.Item.Title
                    )),
            ],
            shown.Count,
            shown.Count > request.Limit,
            choices.Count - shown.Count
        );
    }

    // Roslyn marks the suppression group only by its title, which is English because the bridge requests the en locale
    private static bool IsSuppression(CodeActionChoice choice) =>
        choice.Action["data"]?["CodeActionPath"] is JsonArray { Count: > 0 } path
        && string.Equals(path[0]?.GetValue<string>(), SuppressionGroup, StringComparison.Ordinal);

    private async Task<Result<JsonObject>> SelectionAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        var located = await queries.PositionAsync(request, cancellationToken);
        if (located.Error is { } error)
        {
            return error;
        }

        if (!HasValidEnd(request))
        {
            return ToolError.InvalidRequest("The selection end must follow its start.");
        }

        var parameters = located.Value;
        var start = parameters["position"]!;
        parameters.Remove("position");
        parameters["range"] = new JsonObject
        {
            ["start"] = start,
            ["end"] = EndPosition(request, start),
        };
        return parameters;
    }

    private async Task<Result<JsonObject>> DiagnosticRangeAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        if (
            request
            is not {
                File: not null,
                Symbol: null,
                Character: null,
                EndLine: null,
                EndCharacter: null,
            }
        )
        {
            return ToolError.InvalidRequest(
                "Pass diagnostic with a file and an optional line, without a symbol or character."
            );
        }

        var uri = await session.OpenDocumentAsync(request.File, cancellationToken);
        var report = await session.DiagnosticReportAsync(uri, cancellationToken);
        var diagnostic = (report.Items ?? [])
            .Where(item =>
                string.Equals(
                    item.CodeText(),
                    request.Diagnostic,
                    StringComparison.OrdinalIgnoreCase
                ) && (request.Line is not { } line || item.Range.Start.Line == line - 1)
            )
            .MinBy(item => item.Range.Start);
        if (diagnostic is null)
        {
            var place = request.Line is { } target
                ? string.Create(CultureInfo.InvariantCulture, $" on line {target}")
                : string.Empty;
            return ToolError.InvalidRequest(
                $"{paths.RelativePath(paths.Resolve(request.File))} has no {request.Diagnostic} diagnostic{place}."
            );
        }

        var parameters = LspParameters.Document(uri);
        parameters["range"] = JsonSerializer.SerializeToNode(diagnostic.Range, LspJson.Options);
        return parameters;
    }

    // Nested actions become one entry per child, and fix-all actions one entry per scope
    private static void AddChoices(JsonObject action, List<CodeActionChoice> choices)
    {
        var data = action["data"] as JsonObject;
        if (data?["NestedCodeActions"] is JsonArray { Count: > 0 } nested)
        {
            foreach (var child in nested.OfType<JsonObject>())
            {
                AddChoices(child, choices);
            }

            return;
        }

        var title = ChoiceTitle(action, data);
        var kind = action["kind"]?.GetValue<string>();
        if (data?["FixAllFlavors"] is JsonArray scopes)
        {
            foreach (var scope in scopes.Select(scope => scope!.GetValue<string>()))
            {
                choices.Add(new CodeActionChoice($"{title} ({scope})", kind, action, scope));
            }

            return;
        }

        choices.Add(new CodeActionChoice(title, kind, action, Scope: null));
    }

    // The path names the parent menus of a nested action, and its last part is the action itself
    private static string ChoiceTitle(JsonObject action, JsonObject? data)
    {
        var title = action["title"]?.GetValue<string>() ?? string.Empty;
        if (data?["CodeActionPath"] is not JsonArray { Count: > 1 } path)
        {
            return title;
        }

        return string.Join(
            " > ",
            path.Take(path.Count - 1).Select(part => part!.GetValue<string>()).Append(title)
        );
    }

    private async Task<Result<ToolResult>> SelectActionAsync(
        PendingChange pending,
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        var choices = pending.Actions!;
        if (request.ActionIndex is not >= 0 || request.ActionIndex >= choices.Count)
        {
            return ToolError.InvalidRequest(
                "Select a valid actionIndex from the code action listing."
            );
        }

        // CompleteAsync rejects a stale snapshot before any preview or write
        var choice = choices[request.ActionIndex.Value];
        var action = (JsonObject)choice.Action.DeepClone();
        var resolved = choice switch
        {
            { Scope: { } scope } => await session.RequestAsync(
                LspMethods.CodeActionResolveFixAll,
                new JsonObject
                {
                    ["title"] = action["title"]?.DeepClone(),
                    ["data"] = action["data"]!.DeepClone(),
                    ["scope"] = scope,
                },
                cancellationToken
            ),
            _ when action["data"] is not null => await session.RequestAsync(
                LspMethods.CodeActionResolve,
                action,
                cancellationToken
            ),
            _ => action,
        };
        if (resolved is not JsonObject value)
        {
            return ToolError.InvalidLspData("Roslyn could not resolve the code action.");
        }

        var command = value["command"] switch
        {
            JsonObject commandObject => commandObject,
            JsonValue => value,
            _ => null,
        };
        if (
            command?["command"]?.GetValue<string>() is { } name
            && name.StartsWith(ClientCommandPrefix, StringComparison.Ordinal)
        )
        {
            command = null;
        }

        var selected = new PendingChange(
            request.Action,
            pending.Snapshot,
            value["edit"] as JsonObject,
            command
        );
        if (selected.Edit is null && selected.Command is null)
        {
            return ToolError.InvalidLspData("The selected action contains no edit or command.");
        }

        return await CompleteAsync(selected, request.Apply, cancellationToken);
    }

    // A request with an end also has a line and character, because a symbol cannot take one
    private static bool HasValidEnd(LspRequest request) =>
        request is { EndLine: null, EndCharacter: null }
        || (
            request.EndLine > 0
            && request.EndCharacter > 0
            && request.EndLine >= request.Line
            && (request.EndLine != request.Line || request.EndCharacter >= request.Character)
        );

    private static JsonNode EndPosition(LspRequest request, JsonNode start) =>
        request is { EndLine: { } line, EndCharacter: { } character }
            ? new JsonObject { ["line"] = line - 1, ["character"] = character - 1 }
            : start.DeepClone();
}
