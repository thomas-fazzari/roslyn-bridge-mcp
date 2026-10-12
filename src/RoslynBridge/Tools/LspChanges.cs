// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using RoslynBridge.Editing;
using RoslynBridge.Lsp;

namespace RoslynBridge.Tools;

[DebuggerDisplay("{_pending.Count} pending proposals")]
internal sealed class LspChanges(
    RoslynSession session,
    WorkspacePaths paths,
    WorkspaceEditService edits,
    LspQueries queries
)
{
    internal const int MaximumPendingProposals = 8;
    private static readonly TimeSpan _proposalLifetime = TimeSpan.FromMinutes(5);

    // Insertion order is the creation order, so eviction and expiry start at index 0
    private readonly OrderedDictionary<string, PendingChange> _pending = new(
        StringComparer.Ordinal
    );

    public void Clear() => _pending.Clear();

    public async Task<Result<JsonNode?>> ExecuteAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.ProposalId is not null)
        {
            return await UseProposalAsync(request, cancellationToken);
        }

        return request.Action switch
        {
            LspAction.Rename => await RenameAsync(request, cancellationToken),
            LspAction.RenameFile => await RenameFileAsync(request, cancellationToken),
            LspAction.CodeActions => await CodeActionsAsync(request, cancellationToken),
            _ => throw new InvalidOperationException(
                $"Unsupported change action: {request.Action}."
            ),
        };
    }

    private async Task<Result<JsonNode?>> RenameAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.NewName))
        {
            return ToolError.InvalidRequest("Provide newName for the rename.");
        }

        var snapshot = await CaptureAsync(cancellationToken);
        var position = await queries.PositionAsync(request, cancellationToken);
        if (position.Error is { } error)
        {
            return error;
        }

        var parameters = position.Value;
        parameters["newName"] = request.NewName;
        snapshot = WithDocumentVersions(snapshot);
        if (
            await session.RequestAsync(LspMethods.TextDocumentRename, parameters, cancellationToken)
            is not JsonObject edit
        )
        {
            return ToolError.InvalidLspData("Roslyn cannot rename the selected symbol.");
        }

        return await CompleteAsync(
            new PendingChange(request.Action, snapshot, edit, Command: null),
            request.Apply,
            cancellationToken
        );
    }

    private async Task<Result<JsonNode?>> RenameFileAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.File) || string.IsNullOrWhiteSpace(request.NewName))
        {
            return ToolError.InvalidRequest("Provide file and newName for rename_file.");
        }

        if (session.Capabilities?["workspace"]?["fileOperations"]?["willRename"] is null)
        {
            return ToolError.InvalidRequest("Roslyn does not advertise file rename support.");
        }

        var oldUri = paths.ToUri(request.File);
        var newUri = paths.ToUri(request.NewName);
        var snapshot = await CaptureAsync(cancellationToken);
        var parameters = new JsonObject
        {
            ["files"] = new JsonArray(new JsonObject { ["oldUri"] = oldUri, ["newUri"] = newUri }),
        };
        var edit =
            await session.RequestAsync(
                LspMethods.WorkspaceWillRenameFiles,
                parameters,
                cancellationToken
            ) as JsonObject
            ?? [];
        var changes = ToDocumentChanges(edit);
        changes.Add(
            (JsonNode)
                new JsonObject
                {
                    ["kind"] = WorkspaceEditService.RenameFileOperation,
                    ["oldUri"] = oldUri,
                    ["newUri"] = newUri,
                }
        );
        edit = new JsonObject { ["documentChanges"] = changes };
        return await CompleteAsync(
            new PendingChange(request.Action, snapshot, edit, Command: null, RenamesFiles: true),
            request.Apply,
            cancellationToken
        );
    }

    private async Task<Result<JsonNode?>> CodeActionsAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await CaptureAsync(cancellationToken);
        var located = await queries.PositionAsync(request, cancellationToken);
        if (located.Error is { } error)
        {
            return error;
        }

        if (!HasValidEnd(request))
        {
            return ToolError.InvalidRequest("The selection end must follow its start.");
        }

        var position = located.Value;
        var uri = position["textDocument"]!["uri"]!.GetValue<string>();
        var report = await queries.FileDiagnosticsAsync(uri, cancellationToken);
        snapshot = WithDocumentVersions(snapshot);
        var start = position["position"]!.DeepClone();
        var parameters = new JsonObject
        {
            ["textDocument"] = position["textDocument"]!.DeepClone(),
            ["range"] = new JsonObject { ["start"] = start, ["end"] = EndPosition(request, start) },
            ["context"] = new JsonObject
            {
                ["diagnostics"] = report["items"]?.DeepClone() ?? new JsonArray(),
            },
        };
        var actions =
            await session.RequestAsync(
                LspMethods.TextDocumentCodeAction,
                parameters,
                cancellationToken
            ) as JsonArray
            ?? [];
        var pending = new PendingChange(
            request.Action,
            snapshot,
            Edit: null,
            Command: null,
            actions
        );
        if (request.ActionIndex is not null)
        {
            return await SelectActionAsync(pending, request, cancellationToken);
        }

        if (request.Apply)
        {
            return ToolError.InvalidRequest("Choose actionIndex before applying a code action.");
        }

        var listing = new JsonArray();
        for (var index = 0; index < Math.Min(actions.Count, request.Limit); index++)
        {
            listing.Add(
                (JsonNode)
                    new JsonObject
                    {
                        ["index"] = index,
                        ["title"] = actions[index]?["title"]?.DeepClone(),
                        ["kind"] = actions[index]?["kind"]?.DeepClone(),
                    }
            );
        }

        return new JsonObject
        {
            ["proposalId"] = Remember(pending),
            ["actions"] = listing,
            ["total"] = actions.Count,
            ["truncated"] = actions.Count > request.Limit,
        };
    }

    private async Task<Result<JsonNode?>> UseProposalAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        if (
            !_pending.TryGetValue(request.ProposalId!, out var pending)
            || pending.Action != request.Action
            || DateTimeOffset.UtcNow - pending.CreatedAt > _proposalLifetime
        )
        {
            return ToolError.InvalidRequest(
                "The proposal is unknown or expired. Request a new preview."
            );
        }

        var result = pending.Actions is not null
            ? await SelectActionAsync(pending, request, cancellationToken)
            : await CompleteAsync(pending, request.Apply, cancellationToken);
        if (request.Apply && result.Error is null)
        {
            _pending.Remove(request.ProposalId!);
        }

        return result;
    }

    private async Task<Result<JsonNode?>> SelectActionAsync(
        PendingChange pending,
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        var actions = pending.Actions!;
        if (request.ActionIndex is not >= 0 || request.ActionIndex >= actions.Count)
        {
            return ToolError.InvalidRequest(
                "Select a valid actionIndex from the code action listing."
            );
        }

        // CompleteAsync rejects a stale snapshot before any preview or write
        if (actions[request.ActionIndex.Value]?.DeepClone() is not JsonObject action)
        {
            return ToolError.InvalidLspData("Roslyn returned an invalid code action.");
        }

        if (action["data"] is not null)
        {
            if (
                await session.RequestAsync(LspMethods.CodeActionResolve, action, cancellationToken)
                is not JsonObject resolved
            )
            {
                return ToolError.InvalidLspData("Roslyn could not resolve the code action.");
            }

            action = resolved;
        }

        var command = action["command"] switch
        {
            JsonObject value => value,
            JsonValue => action,
            _ => null,
        };
        var selected = new PendingChange(
            request.Action,
            pending.Snapshot,
            action["edit"] as JsonObject,
            command
        );
        if (selected.Edit is null && selected.Command is null)
        {
            return ToolError.InvalidLspData("The selected action contains no edit or command.");
        }

        return await CompleteAsync(selected, request.Apply, cancellationToken);
    }

    internal async Task<JsonObject> CompleteAsync(
        PendingChange change,
        bool apply,
        CancellationToken cancellationToken
    )
    {
        var edit = change.Edit ?? [];
        if (!apply)
        {
            var preview = await edits.PreviewAsync(edit, change.Snapshot, cancellationToken);
            preview["proposalId"] = Remember(change);
            preview["command"] = change.Command?.DeepClone();
            return EditPreview.Limit(preview, LspTool.PreviewBudgetCharacters);
        }

        var result = await edits.ApplyAsync(edit, change.Snapshot, cancellationToken);
        var phase = change.Command is null
            ? EditApplicationPhase.Synchronize
            : EditApplicationPhase.Command;
        try
        {
            // The reload after a rename replaces a didRenameFiles notification
            var filesChanged = change.RenamesFiles || ChangesFileMembership(result);
            if (change.Command is not null)
            {
                result["commandExecuted"] = false;
                var commandResult = await ExecuteCommandAsync(
                    change.Command,
                    cancellationToken,
                    filesChanged
                );
                result = MergeApplicationResults(result, commandResult);
                result["commandResult"] = commandResult["commandResult"]?.DeepClone();
            }
            else
            {
                await RefreshAfterChangesAsync(filesChanged, cancellationToken);
            }
        }
        catch (EditApplicationException exception)
        {
            throw ApplicationFailure(
                MergeApplicationResults(result, exception.Result),
                exception.Phase,
                exception
            );
        }
        // A failure before any write leaves the workspace unchanged, so it needs no reload
        catch (Exception exception) when (result["fileCount"]!.GetValue<int>() != 0)
        {
            throw ApplicationFailure(result, phase, exception);
        }

        result["synchronized"] = true;
        return result;
    }

    public async Task<JsonObject> ExecuteCommandAsync(
        JsonObject command,
        CancellationToken cancellationToken,
        bool filesChanged = false
    )
    {
        var snapshot = await CaptureAsync(cancellationToken);
        var callbacks = new CommandEdits(edits, snapshot, CaptureAsync, cancellationToken);
        session.ApplyWorkspaceEditAsync = callbacks.ApplyAsync;
        JsonNode? commandResult = null;
        Exception? failure = null;
        var commandExecuted = false;
        try
        {
            commandResult = await session.RequestAsync(
                LspMethods.WorkspaceExecuteCommand,
                command,
                cancellationToken
            );
            commandExecuted = true;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            session.ApplyWorkspaceEditAsync = null;
            await callbacks.DisposeAsync();
        }

        var result = callbacks.Result;
        result["commandExecuted"] = commandExecuted;
        failure = callbacks.Failure ?? failure;
        if (failure is not null)
        {
            if (callbacks.HasChanges)
            {
                throw ApplicationFailure(result, EditApplicationPhase.Command, failure);
            }

            ExceptionDispatchInfo.Throw(failure);
        }

        try
        {
            await RefreshAfterChangesAsync(
                filesChanged || callbacks.ChangesFileMembership,
                cancellationToken
            );
        }
        catch (Exception exception)
        {
            throw ApplicationFailure(result, EditApplicationPhase.Synchronize, exception);
        }

        result["commandResult"] = commandResult;
        result["synchronized"] = true;
        return result;
    }

    internal static JsonObject MergeApplicationResults(JsonObject first, JsonObject second)
    {
        var merged = EditReceipt.Create(
            first["applied"]!.GetValue<bool>() && second["applied"]!.GetValue<bool>(),
            first["files"]!
                .AsArray()
                .Concat(second["files"]!.AsArray())
                .Select(file => file!.DeepClone())
        );
        if (second.TryGetPropertyValue("commandExecuted", out var commandExecuted))
        {
            merged["commandExecuted"] = commandExecuted?.DeepClone();
        }

        return merged;
    }

    private static EditApplicationException ApplicationFailure(
        JsonObject result,
        EditApplicationPhase phase,
        Exception exception
    ) =>
        exception is EditApplicationException application
            ? new EditApplicationException(
                result,
                application.Phase,
                application.FailedPath,
                application.FailedOperation,
                application.InnerException!
            )
            : new EditApplicationException(
                result,
                phase,
                failedPath: null,
                failedOperation: null,
                exception
            );

    private static bool ChangesFileMembership(JsonObject result) =>
        result["files"]!.AsArray().Any(file => EditReceipt.IsFileMembershipChange(file!));

    private async Task RefreshAfterChangesAsync(
        bool filesChanged,
        CancellationToken cancellationToken
    )
    {
        if (filesChanged)
        {
            _pending.Clear();
            await session.ReloadAsync(cancellationToken);
        }
        else
        {
            await session.SynchronizeAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Hashes the workspace and synchronizes Roslyn with it. Call it before the first Roslyn read of a change.
    /// </summary>
    private async Task<WorkspaceSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var snapshot = await edits.CaptureAsync(cancellationToken);
        await session.SynchronizeSnapshotAsync(snapshot.Fingerprints, cancellationToken);
        return WithDocumentVersions(snapshot);
    }

    // Opening a document after the capture adds or evicts versions, so record them again
    // right before the request that computes the edit
    private WorkspaceSnapshot WithDocumentVersions(WorkspaceSnapshot snapshot) =>
        snapshot with
        {
            DocumentVersions = session.CopyDocumentVersions(),
        };

    private string Remember(PendingChange change)
    {
        while (
            _pending.Count != 0
            && (
                _pending.Count >= MaximumPendingProposals
                || DateTimeOffset.UtcNow - _pending.GetAt(0).Value.CreatedAt > _proposalLifetime
            )
        )
        {
            _pending.RemoveAt(0);
        }

        var id = Guid.NewGuid().ToString("N");
        _pending.Add(id, change);
        return id;
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

    private static JsonArray ToDocumentChanges(JsonObject edit)
    {
        if (edit["documentChanges"] is JsonArray documentChanges)
        {
            return (JsonArray)documentChanges.DeepClone();
        }

        var result = new JsonArray();
        if (edit["changes"] is JsonObject changes)
        {
            foreach (var (uri, edits) in changes)
            {
                result.Add(
                    (JsonNode)
                        new JsonObject
                        {
                            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = null },
                            ["edits"] = edits?.DeepClone(),
                        }
                );
            }
        }

        return result;
    }
}
