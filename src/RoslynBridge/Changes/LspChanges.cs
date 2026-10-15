// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
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
    /// Sends the written files to Roslyn without waiting for the file watcher, then waits for
    /// Roslyn to attach created C# files to their projects. Reloads Roslyn when one stays outside.
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
    /// Synchronizes Roslyn with the workspace and records what it received. Call it before the first Roslyn read of a change.
    /// </summary>
    private async Task<WorkspaceSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var files = await session.SynchronizeWorkspaceAsync(cancellationToken);
        return new WorkspaceSnapshot(files, session.CopyDocumentVersions());
    }

    // Opening a document after the capture changes versions, so they are recorded again before the edit request
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
