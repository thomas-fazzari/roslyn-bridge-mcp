// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.ExceptionServices;
using RoslynBridge.Editing;
using RoslynBridge.Lsp;
using RoslynBridge.Queries;
using RoslynBridge.Requests;
using RoslynBridge.Results;

namespace RoslynBridge.Changes;

[DebuggerDisplay("{_pending.Count} pending proposals")]
internal sealed partial class LspChanges(
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

    public async Task<Result<ToolResult>> ExecuteAsync(
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

    internal async Task<ToolResult> CompleteAsync(
        PendingChange change,
        bool apply,
        CancellationToken cancellationToken
    )
    {
        var edit = change.Edit ?? [];
        if (!apply)
        {
            var (files, snapshot) = await edits.PreviewAsync(
                edit,
                change.Snapshot,
                cancellationToken
            );
            return new ProposalPreview(
                Remember(change with { Snapshot = snapshot }),
                files,
                change.Command?.DeepClone()
            );
        }

        var result = await edits.ApplyAsync(edit, change.Snapshot, cancellationToken);
        var phase = change.Command is null
            ? EditApplicationPhase.Synchronize
            : EditApplicationPhase.Command;
        try
        {
            if (change.Command is not null)
            {
                result = result with { CommandExecuted = false };
                var commandResult = await ExecuteCommandAsync(change.Command, cancellationToken);
                result = result.Merge(commandResult) with
                {
                    CommandResult = commandResult.CommandResult,
                };
            }
            else
            {
                await SynchronizeAsync(result, cancellationToken);
            }
        }
        catch (EditApplicationException exception)
        {
            throw ApplicationFailure(result.Merge(exception.Result), exception.Phase, exception);
        }
        // A failure before any write leaves the workspace unchanged, so it needs no reload
        catch (Exception exception) when (result.Operations.Count != 0)
        {
            throw ApplicationFailure(result, phase, exception);
        }

        return result with
        {
            Synchronized = true,
        };
    }

    public async Task<EditReceipt> ExecuteCommandAsync(
        JsonObject command,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await CaptureAsync(cancellationToken);
        var callbacks = new CommandEdits(edits, snapshot, CaptureAsync, cancellationToken);
        JsonNode? commandResult = null;
        Exception? failure = null;
        var commandExecuted = false;
        using (session.HandleWorkspaceEdits(callbacks.ApplyAsync))
        {
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
        }

        await callbacks.DisposeAsync();

        var result = callbacks.Result with { CommandExecuted = commandExecuted };
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
            await SynchronizeAsync(result, cancellationToken);
        }
        catch (Exception exception)
        {
            throw ApplicationFailure(result, EditApplicationPhase.Synchronize, exception);
        }

        return result with
        {
            CommandResult = commandResult,
            Synchronized = true,
        };
    }

    private async Task<Result<ToolResult>> RenameAsync(
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
        snapshot = await WithDocumentVersionsAsync(snapshot, cancellationToken);
        if (
            await session.RequestAsync(LspMethods.TextDocumentRename, parameters, cancellationToken)
            is not JsonObject edit
        )
        {
            return await RenameRefusalAsync(parameters, request.NewName, cancellationToken);
        }

        return await CompleteAsync(
            new PendingChange(request.Action, snapshot, edit, Command: null),
            request.Apply,
            cancellationToken
        );
    }

    // Roslyn returns no edit both for a position without a symbol and for a name it rejects
    private async Task<ToolError> RenameRefusalAsync(
        JsonObject parameters,
        string newName,
        CancellationToken cancellationToken
    )
    {
        var position = new JsonObject
        {
            ["textDocument"] = parameters["textDocument"]!.DeepClone(),
            ["position"] = parameters["position"]!.DeepClone(),
        };
        if (
            await session.RequestAsync(
                LspMethods.TextDocumentPrepareRename,
                position,
                cancellationToken
            ) is JsonObject
        )
        {
            return ToolError.InvalidRequest(
                $"Roslyn rejects '{newName}' as the new name. Pass one valid C# identifier, without dots."
            );
        }

        return await queries.MissingSymbolAsync(position, cancellationToken)
            ?? ToolError.InvalidRequest(
                "Roslyn cannot rename this symbol. It may be declared outside the workspace."
            );
    }

    private async Task<Result<ToolResult>> RenameFileAsync(
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
                    ["kind"] = FileOperation.Rename,
                    ["oldUri"] = oldUri,
                    ["newUri"] = newUri,
                }
        );
        edit = new JsonObject { ["documentChanges"] = changes };
        return await CompleteAsync(
            new PendingChange(request.Action, snapshot, edit, Command: null),
            request.Apply,
            cancellationToken
        );
    }

    private async Task<Result<ToolResult>> UseProposalAsync(
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

    private static EditApplicationException ApplicationFailure(
        EditReceipt result,
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

    /// <summary>
    /// Reloads Roslyn when a created C# file stays outside every project.
    /// </summary>
    private async Task SynchronizeAsync(EditReceipt receipt, CancellationToken cancellationToken)
    {
        var written = receipt
            .Operations.SelectMany(operation =>
                operation.OldPath is { } oldPath
                    ? [operation.Path, oldPath]
                    : (string[])[operation.Path]
            )
            .Select(paths.Resolve)
            .ToList();
        await session.SynchronizeFilesAsync(written, cancellationToken);

        string[] created =
        [
            .. receipt
                .Operations.Where(operation =>
                    operation.Kind is FileOperation.Create or FileOperation.Rename
                )
                .Select(operation => paths.Resolve(operation.Path))
                .Where(path => RoslynSession.IsCSharpFile(path) && File.Exists(path)),
        ];
        if (
            created.Length != 0
            && (await session.WaitForProjectsAsync(created, cancellationToken)).Count != 0
        )
        {
            _pending.Clear();
            await session.ReloadAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Call before the first Roslyn read of a change.
    /// </summary>
    private async Task<WorkspaceSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var files = await session.SynchronizeWorkspaceAsync(cancellationToken);
        return new WorkspaceSnapshot(
            files,
            await session.CopyDocumentVersionsAsync(cancellationToken)
        );
    }

    // Opening a document after the capture changes versions, so they are recorded again before the edit request
    private async Task<WorkspaceSnapshot> WithDocumentVersionsAsync(
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken
    ) =>
        snapshot with
        {
            DocumentVersions = await session.CopyDocumentVersionsAsync(cancellationToken),
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
