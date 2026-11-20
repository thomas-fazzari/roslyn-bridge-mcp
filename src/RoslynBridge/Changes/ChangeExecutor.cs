// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.Logging;
using RoslynBridge.Editing;
using RoslynBridge.Lsp;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Results;
using RoslynBridge.Workspace;

namespace RoslynBridge.Changes;

/// <summary>
/// Previews and applies changes. A failure after a write makes Roslyn reload the workspace from disk.
/// </summary>
internal sealed partial class ChangeExecutor(
    RoslynSession session,
    WorkspacePaths paths,
    WorkspaceEditService edits,
    DiagnosticScanner diagnostics,
    ProposalStore proposals,
    ILogger<ChangeExecutor> logger
)
{
    private const int MaximumCheckedFiles = 128;

    /// <summary>
    /// Runs a command of a raw request. It adds no diagnostics.
    /// </summary>
    public async Task<Result<EditReceipt>> ExecuteCommandAsync(
        LspCommand command,
        CancellationToken cancellationToken
    )
    {
        var generation = session.Generation;
        var result = await RunCommandAsync(command, cancellationToken) switch
        {
            ToolError error => error,
            EditReceipt receipt => await SynchronizedAsync(receipt, cancellationToken),
        };
        return RecoveredOnFailure(result, generation);
    }

    /// <summary>
    /// Previews <paramref name="change"/> as a new proposal, or applies it and adds the diagnostics of the changed files.
    /// </summary>
    public async Task<Result<ToolResult>> CompleteAsync(
        ProposedEdit change,
        bool apply,
        CancellationToken cancellationToken
    )
    {
        if (!apply)
            return await PreviewAsync(change, cancellationToken);

        var generation = session.Generation;
        return RecoveredOnFailure(await ApplyAsync(change, cancellationToken), generation) switch
        {
            ToolError error => error,
            EditReceipt receipt => await WithDiagnosticsAsync(receipt, cancellationToken),
        };
    }

    /// <summary>
    /// Resolves the code action at <paramref name="actionIndex"/> and completes its edit.
    /// </summary>
    public async Task<Result<ToolResult>> CompleteAsync(
        ActionChoices choices,
        int? actionIndex,
        bool apply,
        CancellationToken cancellationToken
    )
    {
        if (
            actionIndex is not { } index
            || choices.Actions.ElementAtOrDefault(index) is not { } choice
        )
        {
            return ToolError.InvalidRequest(
                "Select a valid actionIndex from the code action listing."
            );
        }

        // CompleteAsync rejects a stale snapshot before any preview or write
        var resolution = await session.ResolveCodeActionAsync(choice, cancellationToken);
        if (resolution.IsError(out var error, out var resolved))
            return error;

        var selected = new ProposedEdit(
            choices.Action,
            choices.Snapshot,
            resolved.Edit,
            resolved.Command
        );
        return await CompleteAsync(selected, apply, cancellationToken);
    }

    /// <summary>
    /// Call before the first Roslyn read of a change.
    /// </summary>
    public async Task<Result<WorkspaceSnapshot>> CaptureAsync(
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

    /// <summary>
    /// Captures the workspace, then runs <paramref name="locate"/>, which may open documents before the edit request.
    /// </summary>
    public async Task<Result<(WorkspaceSnapshot Snapshot, T Located)>> CaptureAsync<T>(
        Func<CancellationToken, Task<Result<T>>> locate,
        CancellationToken cancellationToken
    )
    {
        var captured = await CaptureAsync(cancellationToken);
        if (captured.IsError(out var captureError, out var snapshot))
            return captureError;

        var located = await locate(cancellationToken);
        if (located.IsError(out var error, out var value))
            return error;

        // Opening a document changes its version, so the versions are recorded again
        var versions = await session.CopyDocumentVersionsAsync(cancellationToken);
        return (snapshot with { DocumentVersions = versions }, value);
    }

    private async Task<Result<ToolResult>> PreviewAsync(
        ProposedEdit change,
        CancellationToken cancellationToken
    ) =>
        await edits.PreviewAsync(change.Edit, change.Snapshot, cancellationToken) switch
        {
            ToolError error => error,
            EditPreview preview => new ProposalPreview(
                proposals.Remember(change with { Snapshot = preview.Snapshot }),
                preview.Files,
                change.Command?.ToJson()
            ),
        };

    private async Task<Result<EditReceipt>> ApplyAsync(
        ProposedEdit change,
        CancellationToken cancellationToken
    )
    {
        var applying = await edits.ApplyAsync(change.Edit, change.Snapshot, cancellationToken);
        if (applying.IsError(out var applyError, out var applied))
            return applyError;

        var result = applied;
        var phase = EditApplicationPhase.Command;
        try
        {
            if (change.Command is not null)
            {
                result = result with { Command = new CommandRun(Executed: false) };
                var executed = await RunCommandAsync(change.Command, cancellationToken);
                if (executed.IsError(out var commandError, out var commandReceipt))
                {
                    return commandError is EditFailure failure
                        ? Failed(result.Merge(failure.Receipt), phase, failure)
                        : AfterWrites(result, phase, commandError);
                }

                result = result.Merge(commandReceipt);
            }

            // One synchronization covers the edit and the command callbacks
            phase = EditApplicationPhase.Synchronize;
            return await SynchronizedAsync(result, cancellationToken);
        }
        catch (OperationCanceledException exception) when (result.Operations.Count != 0)
        {
            return Failed(result, phase, ToolError.Cancelled(exception));
        }
    }

    // The edit is already applied, so a failed check returns the receipt without diagnostics
    private async Task<EditReceipt> WithDiagnosticsAsync(
        EditReceipt receipt,
        CancellationToken cancellationToken
    )
    {
        string[] files =
        [
            .. receipt
                .Operations.Select(operation => paths.FullPath(operation.Path))
                .Distinct(StringComparer.Ordinal)
                .Where(WorkspacePaths.IsExistingCSharpFile),
        ];
        if (files.Length is 0 or > MaximumCheckedFiles)
            return receipt;

        var checkedFiles = await diagnostics
            .ScanAsync(files, DiagnosticFilter.Default, cancellationToken)
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

    // Files changed outside the bridge's synchronization, so Roslyn starts again from disk
    private Result<EditReceipt> RecoveredOnFailure(Result<EditReceipt> result, int generation)
    {
        if (result is EditFailure)
            session.RequireReload(generation);

        return result;
    }

    private async Task<Result<EditReceipt>> RunCommandAsync(
        LspCommand command,
        CancellationToken cancellationToken
    )
    {
        var capture = await CaptureAsync(cancellationToken);
        if (capture.IsError(out var captureError, out var captured))
            return captureError;

        await using var callbacks = new CommandEdits(
            edits,
            captured,
            CaptureAsync,
            cancellationToken
        );
        var executed = await session
            .ExecuteCommandAsync(command, callbacks.ApplyAsync, cancellationToken)
            .OrCancelledAsync();

        // Late callbacks must finish before the receipt is read
        await callbacks.StopAsync();

        var executeFailed = executed.IsError(out var executeError, out var commandResult);
        var result = callbacks.Result with { Command = new CommandRun(!executeFailed) };
        if ((callbacks.Failure ?? executeError) is { } commandError)
        {
            return callbacks.HasChanges
                ? Failed(result, EditApplicationPhase.Command, commandError)
                : commandError;
        }

        return result with
        {
            Command = new CommandRun(Executed: true, commandResult),
        };
    }

    private async Task<Result<EditReceipt>> SynchronizedAsync(
        EditReceipt receipt,
        CancellationToken cancellationToken
    ) =>
        await SynchronizeAsync(receipt, cancellationToken).OrCancelledAsync() is ToolError error
            ? AfterWrites(receipt, EditApplicationPhase.Synchronize, error)
            : receipt with
            {
                Synchronized = true,
            };

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
                    operation.Kind is FileChange.Create or FileChange.Rename
                )
                .Select(operation => paths.FullPath(operation.Path))
                .Where(WorkspacePaths.IsExistingCSharpFile),
        ];
        if (created.Length == 0)
            return Result.Success;

        var waited = await session.WaitForProjectsAsync(created, cancellationToken);
        if (waited.IsError(out var waitError, out var outside))
            return waitError;

        return outside.Count == 0 ? Result.Success : await session.ReloadAsync(cancellationToken);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "LSP diagnostics failed with {Code}: {Reason}"
    )]
    private static partial void DiagnosticsFailed(ILogger logger, string code, string reason);
}
