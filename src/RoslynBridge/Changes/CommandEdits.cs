// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Editing;
using RoslynBridge.Lsp;
using RoslynBridge.Results;

namespace RoslynBridge.Changes;

internal sealed class CommandEdits(
    WorkspaceEditService edits,
    WorkspaceSnapshot snapshot,
    Func<CancellationToken, Task<WorkspaceSnapshot>> capture,
    CancellationToken cancellationToken
) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime =
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _admission = new();
    private readonly List<EditReceipt> _receipts = [];
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);

    // Roslyn's edit callbacks arrive outside the tool call, so their traces join the call that ran the command
    private readonly ActivityContext _call = Activity.Current?.Context ?? default;

    private WorkspaceSnapshot _snapshot = snapshot;
    private TaskCompletionSource? _callbacksDrained;
    private Exception? _failure;
    private int _activeCallbacks;
    private bool _accepting = true;
    private bool _applied = true;

    public Exception? Failure => _failure;

    public bool HasChanges => _paths.Count != 0;

    public EditReceipt Result
    {
        get
        {
            lock (_admission)
            {
                return BuildResult();
            }
        }
    }

    public async Task<ApplyWorkspaceEditResult> ApplyAsync(
        JsonObject edit,
        CancellationToken callbackToken
    )
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (!TryBeginCallback())
        {
            return RejectedResponse();
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token,
                callbackToken
            );
            try
            {
                await _gate.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException exception)
            {
                return IsAcceptingEdits()
                    ? FailCallback(exception, EditApplicationPhase.Write, applied: false)
                    : RejectedResponse();
            }

            try
            {
                return IsAcceptingEdits()
                    ? await ApplyAndCaptureAsync(edit, linked.Token)
                    : RejectedResponse();
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            EndCallback();
        }
    }

    private async Task<ApplyWorkspaceEditResult> ApplyAndCaptureAsync(
        JsonObject edit,
        CancellationToken token
    )
    {
        EditReceipt receipt;
        try
        {
            receipt = await edits.ApplyAsync(edit, _snapshot, token, parent: _call);
        }
        catch (EditApplicationException exception)
        {
            RecordReceipt(exception.Result);
            return FailCallback(exception, exception.Phase, applied: exception.Result.Applied);
        }
        catch (Exception exception)
        {
            return FailCallback(exception, EditApplicationPhase.Write, applied: false);
        }

        RecordReceipt(receipt);
        try
        {
            _snapshot = await capture(token);
            return new ApplyWorkspaceEditResult(Applied: true);
        }
        catch (Exception exception)
        {
            return FailCallback(exception, EditApplicationPhase.Synchronize, applied: true);
        }
    }

    public async Task StopAsync()
    {
        Task drained;
        lock (_admission)
        {
            _accepting = false;
            drained = _callbacksDrained?.Task ?? Task.CompletedTask;
        }

        await _lifetime.CancelAsync();
        await drained;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifetime.Dispose();
        _gate.Dispose();
    }

    private bool TryBeginCallback()
    {
        lock (_admission)
        {
            if (!_accepting)
            {
                return false;
            }

            if (_activeCallbacks++ == 0)
            {
                _callbacksDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
            }

            return true;
        }
    }

    private void EndCallback()
    {
        lock (_admission)
        {
            if (--_activeCallbacks == 0)
            {
                _callbacksDrained!.TrySetResult();
            }
        }
    }

    private bool IsAcceptingEdits()
    {
        lock (_admission)
        {
            return _accepting;
        }
    }

    private void RecordReceipt(EditReceipt receipt)
    {
        lock (_admission)
        {
            _receipts.Add(receipt);
            _paths.UnionWith(receipt.Operations.Select(operation => operation.Path));
        }
    }

    private ApplyWorkspaceEditResult FailCallback(
        Exception exception,
        EditApplicationPhase phase,
        bool applied
    )
    {
        lock (_admission)
        {
            _applied &= applied;
            _failure ??=
                applied && exception is not EditApplicationException
                    ? new EditApplicationException(
                        BuildResult(),
                        phase,
                        failedPath: null,
                        failedOperation: null,
                        exception
                    )
                    : exception;

            _accepting = false;
            return new ApplyWorkspaceEditResult(
                applied,
                _failure.InnerException?.Message ?? _failure.Message
            );
        }
    }

    private EditReceipt BuildResult() =>
        new(_applied, [.. _receipts.SelectMany(receipt => receipt.Operations)]);

    private static ApplyWorkspaceEditResult RejectedResponse() =>
        new(Applied: false, "The workspace edit callback is no longer accepting edits.");
}
