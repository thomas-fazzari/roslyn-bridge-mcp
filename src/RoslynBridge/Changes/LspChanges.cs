// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.Logging;
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
    LspQueries queries,
    ILogger<LspChanges> logger
)
{
    internal const int MaximumPendingProposals = 8;
    private const int MaximumCheckedFiles = 128;
    private static readonly TimeSpan _proposalLifetime = TimeSpan.FromMinutes(5);

    // Insertion order is the creation order, so eviction and expiry start at index 0
    private readonly OrderedDictionary<string, PendingChange> _pending =
    [
        with(StringComparer.Ordinal),
    ];

    public void Clear() => _pending.Clear();

    public async Task<Result<ToolResult>> ExecuteAsync(
        EditRequest request,
        CancellationToken cancellationToken
    )
    {
        var routed = await RouteAsync(request, cancellationToken);
        var result = await WithDiagnosticsAsync(routed, cancellationToken);
        if (result is EditFailure)
            RecoverFromPartialApplication();

        return result;
    }

    /// <summary>
    /// Runs a command of a raw request. It adds no diagnostics.
    /// </summary>
    public async Task<Result<EditReceipt>> ExecuteCommandAsync(
        JsonObject command,
        CancellationToken cancellationToken
    )
    {
        var result = await RunCommandAsync(command, cancellationToken);
        if (result is EditFailure)
            RecoverFromPartialApplication();

        return result;
    }

    internal async Task<Result<ToolResult>> CompleteAsync(
        PendingChange change,
        bool apply,
        CancellationToken cancellationToken
    )
    {
        if (!apply)
        {
            return await edits.PreviewAsync(change.Edit, change.Snapshot, cancellationToken) switch
            {
                ToolError previewError => previewError,
                EditPreview preview => new ProposalPreview(
                    Remember(change with { Snapshot = preview.Snapshot }),
                    preview.Files,
                    change.Command?.DeepClone()
                ),
            };
        }

        if (
            (await edits.ApplyAsync(change.Edit, change.Snapshot, cancellationToken)).IsError(
                out var applyError,
                out var applied
            )
        )
        {
            return applyError;
        }

        var result = applied;
        var phase = EditApplicationPhase.Command;
        try
        {
            if (change.Command is not null)
            {
                result = result with { CommandExecuted = false };
                if (
                    (await RunCommandAsync(change.Command, cancellationToken)).IsError(
                        out var commandError,
                        out var commandReceipt
                    )
                )
                {
                    if (commandError is EditFailure failure)
                        return Failed(result.Merge(failure.Receipt), phase, failure);

                    return AfterWrites(result, phase, commandError);
                }

                result = result.Merge(commandReceipt) with
                {
                    CommandResult = commandReceipt.CommandResult,
                };
            }

            // RunCommandAsync synchronizes only the edits of the command's callbacks
            phase = EditApplicationPhase.Synchronize;
            if (await SynchronizeAsync(applied, cancellationToken) is ToolError error)
                return AfterWrites(result, phase, error);
        }
        catch (OperationCanceledException exception) when (result.Operations.Count != 0)
        {
            return Failed(result, phase, ToolError.Cancelled(exception));
        }

        return result with
        {
            Synchronized = true,
        };
    }

    private async Task<Result<ToolResult>> RouteAsync(
        EditRequest request,
        CancellationToken cancellationToken
    ) =>
        request switch
        {
            { ProposalId: not null } => await UseProposalAsync(request, cancellationToken),
            RenameRequest rename => await RenameAsync(rename, cancellationToken),
            RenameFileRequest renameFile => await RenameFileAsync(renameFile, cancellationToken),
            CodeActionsRequest codeActions => await CodeActionsAsync(
                codeActions,
                cancellationToken
            ),
        };

    private async Task<Result<EditReceipt>> RunCommandAsync(
        JsonObject command,
        CancellationToken cancellationToken
    )
    {
        if ((await CaptureAsync(cancellationToken)).IsError(out var captureError, out var captured))
            return captureError;

        var callbacks = new CommandEdits(edits, captured, CaptureAsync, cancellationToken);
        Result<JsonNode?> executed;
        using (session.HandleWorkspaceEdits(callbacks.ApplyAsync))
        {
            executed = await session
                .RequestAsync(LspMethods.WorkspaceExecuteCommand, command, cancellationToken)
                .OrCancelledAsync();
        }

        await callbacks.DisposeAsync();

        var executeFailed = executed.IsError(out var executeError, out var commandResult);
        var result = callbacks.Result with { CommandExecuted = !executeFailed };
        if ((callbacks.Failure ?? executeError) is { } commandError)
        {
            return callbacks.HasChanges
                ? Failed(result, EditApplicationPhase.Command, commandError)
                : commandError;
        }

        var synchronized = await SynchronizeAsync(result, cancellationToken).OrCancelledAsync();
        if (synchronized is ToolError error)
            return Failed(result, EditApplicationPhase.Synchronize, error);

        return result with
        {
            CommandResult = commandResult,
            Synchronized = true,
        };
    }

    private async Task<Result<ToolResult>> RenameAsync(
        RenameRequest request,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.NewName))
            return ToolError.InvalidRequest("Provide newName for the rename.");

        if ((await CaptureAsync(cancellationToken)).IsError(out var captureError, out var snapshot))
            return captureError;

        if (
            (
                await queries.PositionAsync(request.Target, request.Action, cancellationToken)
            ).IsError(out var error, out var parameters)
        )
        {
            return error;
        }

        parameters["newName"] = request.NewName;
        var versioned = await WithDocumentVersionsAsync(snapshot, cancellationToken);
        var renamed = await session.RequestAsync(
            LspMethods.TextDocumentRename,
            parameters,
            cancellationToken
        );
        if (renamed.IsError(out var renameError, out var answer))
            return renameError;

        if (answer is not JsonObject json)
            return await RenameRefusalAsync(parameters, request.NewName, cancellationToken);

        if (WorkspaceEdit.Parse(json).IsError(out var editError, out var edit))
            return editError;

        return await CompleteAsync(
            new PendingChange(request.Action, versioned, edit, Command: null),
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
            (
                await session.RequestAsync(
                    LspMethods.TextDocumentPrepareRename,
                    position,
                    cancellationToken
                )
            ).IsError(out var error, out var prepared)
        )
        {
            return error;
        }

        if (prepared is JsonObject)
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
        RenameFileRequest request,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.File) || string.IsNullOrWhiteSpace(request.NewName))
            return ToolError.InvalidRequest("Provide file and newName for rename_file.");

        if (session.Capabilities?["workspace"]?["fileOperations"]?["willRename"] is null)
            return ToolError.InvalidRequest("Roslyn does not advertise file rename support.");

        if (paths.ToUri(request.File).IsError(out var oldError, out var oldUri))
            return oldError;

        if (paths.ToUri(request.NewName).IsError(out var newError, out var newUri))
            return newError;

        if ((await CaptureAsync(cancellationToken)).IsError(out var captureError, out var snapshot))
            return captureError;

        var parameters = new JsonObject
        {
            ["files"] = new JsonArray(new JsonObject { ["oldUri"] = oldUri, ["newUri"] = newUri }),
        };
        if (
            (
                await session.RequestAsync(
                    LspMethods.WorkspaceWillRenameFiles,
                    parameters,
                    cancellationToken
                )
            ).IsError(out var renameError, out var renamed)
        )
        {
            return renameError;
        }

        var parsed = WorkspaceEdit.Parse(renamed as JsonObject);
        if (parsed.IsError(out var editError, out var willRename))
            return editError;

        var edit = new WorkspaceEdit([
            .. willRename.Operations,
            new RenameFile(oldUri, newUri, new FileOperationOptions()),
        ]);
        return await CompleteAsync(
            new PendingChange(request.Action, snapshot, edit, Command: null),
            request.Apply,
            cancellationToken
        );
    }

    private async Task<Result<ToolResult>> UseProposalAsync(
        EditRequest request,
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

        // Only a code action listing holds actions
        var result =
            request is CodeActionsRequest codeActions && pending.Actions is not null
                ? await SelectActionAsync(
                    pending,
                    codeActions.ActionIndex,
                    request.Apply,
                    cancellationToken
                )
                : await CompleteAsync(pending, request.Apply, cancellationToken);
        if (request.Apply && result is not ToolError)
            _pending.Remove(request.ProposalId!);

        return result;
    }

    /// <summary>
    /// Keeps the step, path and operation of an earlier failure.
    /// </summary>
    private static EditFailure Failed(
        EditReceipt receipt,
        EditApplicationPhase phase,
        ToolError error
    ) =>
        error is EditFailure failure
            ? failure with
            {
                Receipt = receipt,
            }
            : new EditFailure(error, receipt, phase);

    // A failure before any write leaves the workspace unchanged, so it needs no reload
    private static ToolError AfterWrites(
        EditReceipt receipt,
        EditApplicationPhase phase,
        ToolError error
    ) => receipt.Operations.Count == 0 ? error : Failed(receipt, phase, error);

    /// <summary>
    /// Reloads Roslyn when a created C# file stays outside every project.
    /// </summary>
    private async Task<Result> SynchronizeAsync(
        EditReceipt receipt,
        CancellationToken cancellationToken
    )
    {
        var written = receipt
            .Operations.SelectMany(operation =>
                operation.OldPath is { } oldPath
                    ? [operation.Path, oldPath]
                    : (string[])[operation.Path]
            )
            .Select(paths.FullPath)
            .ToList();
        if (await session.SynchronizeFilesAsync(written, cancellationToken) is ToolError error)
            return error;

        string[] created =
        [
            .. receipt
                .Operations.Where(operation =>
                    operation.Kind is FileOperation.Create or FileOperation.Rename
                )
                .Select(operation => paths.FullPath(operation.Path))
                .Where(path => WorkspacePaths.IsCSharpFile(path) && File.Exists(path)),
        ];
        if (created.Length == 0)
            return Result.Success;

        if (
            (await session.WaitForProjectsAsync(created, cancellationToken)).IsError(
                out var waitError,
                out var outside
            )
        )
        {
            return waitError;
        }

        if (outside.Count == 0)
            return Result.Success;

        _pending.Clear();
        return await session.ReloadAsync(cancellationToken);
    }

    /// <summary>
    /// Call before the first Roslyn read of a change.
    /// </summary>
    private async Task<Result<WorkspaceSnapshot>> CaptureAsync(
        CancellationToken cancellationToken
    ) =>
        await session.SynchronizeWorkspaceAsync(cancellationToken) switch
        {
            ToolError error => error,
            Dictionary<string, FileStamp> files => new WorkspaceSnapshot(
                files,
                await session.CopyDocumentVersionsAsync(cancellationToken)
            ),
        };

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

    private void RecoverFromPartialApplication()
    {
        // Files changed outside the bridge's synchronization, so Roslyn starts again from disk
        _pending.Clear();
        session.RequireReload();
    }

    /// <summary>
    /// Adds the diagnostics of the changed files to an applied edit.
    /// </summary>
    private async Task<Result<ToolResult>> WithDiagnosticsAsync(
        Result<ToolResult> result,
        CancellationToken cancellationToken
    )
    {
        if (result is not EditReceipt { Synchronized: true } receipt)
            return result;

        string[] files =
        [
            .. receipt
                .Operations.Select(operation => paths.FullPath(operation.Path))
                .Distinct(StringComparer.Ordinal)
                .Where(path => WorkspacePaths.IsCSharpFile(path) && File.Exists(path)),
        ];
        if (files.Length is 0 or > MaximumCheckedFiles)
            return receipt;

        // The edit is already applied, so a failed check returns the receipt without diagnostics
        var checkedFiles = await queries
            .DiagnosticsAsync(
                files,
                new DiagnosticsRequest(
                    File: null,
                    DiagnosticSeverity.Warning,
                    Ids: null,
                    Summary: false,
                    Paging.Default
                ),
                cancellationToken
            )
            .OrCancelledAsync();
        if (checkedFiles.IsError(out var error, out var report))
        {
            DiagnosticsFailed(logger, error.Code, error.Message);
            return receipt;
        }

        return receipt with
        {
            Diagnostics = report,
        };
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "LSP diagnostics failed with {Code}: {Reason}"
    )]
    private static partial void DiagnosticsFailed(ILogger logger, string code, string reason);
}
