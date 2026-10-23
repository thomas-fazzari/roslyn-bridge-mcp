// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using RoslynBridge.Changes;
using RoslynBridge.Editing;
using RoslynBridge.Lsp;
using RoslynBridge.Output;
using RoslynBridge.Queries;
using RoslynBridge.Requests;
using RoslynBridge.Results;
using StreamJsonRpc;

namespace RoslynBridge.Tools;

internal sealed partial class LspTool(
    BridgeOptions options,
    WorkspacePaths paths,
    RoslynSession session,
    LspQueries queries,
    LspChanges changes,
    ILogger<LspTool> logger
) : IDisposable
{
    private const int MaximumErrorCharacters = 2_000;

    private static readonly FrozenSet<string> _readOnlyMethods = new[]
    {
        LspMethods.TextDocumentDefinition,
        LspMethods.TextDocumentTypeDefinition,
        LspMethods.TextDocumentImplementation,
        LspMethods.TextDocumentReferences,
        LspMethods.TextDocumentHover,
        LspMethods.TextDocumentDocumentHighlight,
        LspMethods.TextDocumentSelectionRange,
        LspMethods.TextDocumentInlayHint,
        LspMethods.TextDocumentFoldingRange,
        LspMethods.TextDocumentSemanticTokensFull,
        LspMethods.TextDocumentSemanticTokensRange,
        LspMethods.TextDocumentCodeLens,
        LspMethods.TextDocumentDocumentSymbol,
        LspMethods.TextDocumentCompletion,
        LspMethods.CompletionItemResolve,
        LspMethods.TextDocumentSignatureHelp,
        LspMethods.TextDocumentCodeAction,
        LspMethods.CodeActionResolve,
        LspMethods.CodeActionResolveFixAll,
        LspMethods.TextDocumentPrepareRename,
        LspMethods.TextDocumentRename,
        LspMethods.TextDocumentDiagnostic,
        LspMethods.TextDocumentGetProjectContexts,
        LspMethods.WorkspaceDiagnostic,
        LspMethods.WorkspaceSymbol,
        LspMethods.TextDocumentPrepareCallHierarchy,
        LspMethods.CallHierarchyIncomingCalls,
        LspMethods.CallHierarchyOutgoingCalls,
        LspMethods.TextDocumentPrepareTypeHierarchy,
        LspMethods.TypeHierarchySupertypes,
        LspMethods.TypeHierarchySubtypes,
        LspMethods.TextDocumentFormatting,
        LspMethods.TextDocumentRangeFormatting,
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<CallToolResult> ExecuteAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.Action is LspAction.Status)
        {
            return Response(Status());
        }

        await _gate.WaitAsync(cancellationToken);
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(
                session.IsReady && !MayRestartServer(request)
                    ? options.RequestTimeout
                    : options.StartupTimeout + options.RequestTimeout
            );
            var resolved = await ResolveFilesAsync(request, timeout.Token);
            if (resolved.Error is { } fileError)
            {
                return Failure(fileError);
            }

            var result = await DispatchAsync(
                resolved.Value.Request,
                resolved.Value.FileErrors,
                timeout.Token
            );
            if (result.Error is { } error)
            {
                return Failure(error);
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                OperationCompleted(logger, request.Action, elapsedMilliseconds);
            }

            return Response(result.Value, resolved.Value.Files);
        }
        catch (EditApplicationException exception)
        {
            return ReportApplicationFailure(request.Action, exception, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                new ToolError(
                    ErrorCodes.Timeout,
                    "The LSP operation timed out. No successful result is available."
                )
            );
        }
        catch (RemoteInvocationException exception)
        {
            return Failure(new ToolError(ErrorCodes.Lsp, exception.Message), exception.ErrorCode);
        }
        catch (Exception exception) when (ToolFailures.IsReported(exception))
        {
            return ReportFailure(request.Action, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private CallToolResult ReportFailure(LspAction action, Exception exception)
    {
        OperationFailed(logger, action, exception);
        return Failure(ToolFailures.ToError(exception));
    }

    /// <summary>
    /// Adds the diagnostics of the changed files to an applied edit, so checking it needs no further call.
    /// </summary>
    private async Task<Result<ToolResult>> WithDiagnosticsAsync(
        Result<ToolResult> result,
        CancellationToken cancellationToken
    )
    {
        if (result.Value is not EditReceipt { Synchronized: true } receipt)
        {
            return result;
        }

        string[] files =
        [
            .. receipt
                .Operations.Select(operation => paths.Resolve(operation.Path))
                .Distinct(StringComparer.Ordinal)
                .Where(path => RoslynSession.IsCSharpFile(path) && File.Exists(path)),
        ];
        if (files.Length is 0 or > RoslynSession.MaximumOpenDocuments)
        {
            return receipt;
        }

        try
        {
            return receipt with
            {
                Diagnostics = await queries.DiagnosticsAsync(
                    files,
                    new LspRequest { Action = LspAction.Diagnostics },
                    cancellationToken
                ),
            };
        }
        // The edit is already applied, so a failed check returns the receipt without diagnostics
        catch (Exception exception)
            when (ToolFailures.IsReported(exception) || exception is OperationCanceledException)
        {
            OperationFailed(logger, LspAction.Diagnostics, exception);
            return receipt;
        }
    }

    private CallToolResult ReportApplicationFailure(
        LspAction action,
        EditApplicationException exception,
        CancellationToken cancellationToken
    )
    {
        changes.Clear();
        session.RequireReload();
        OperationFailed(logger, action, exception);
        return ApplicationFailure(exception, cancellationToken.IsCancellationRequested);
    }

    private static bool MayRestartServer(LspRequest request) =>
        request.Action is LspAction.Reload || request.Apply;

    // Diagnostics and a new edit synchronize Roslyn themselves, so an earlier synchronization repeats work
    private static bool SynchronizesBeforeFirstRead(LspRequest request) =>
        request.Action is LspAction.Diagnostics
        || (request.ProposalId is null && request.Action.IsChange);

    private async Task<Result<ToolResult>> DispatchAsync(
        LspRequest request,
        IReadOnlyDictionary<string, ToolError> fileErrors,
        CancellationToken cancellationToken
    )
    {
        if (request.Limit is < 1 or > LspRequest.MaximumResultLimit)
        {
            return ToolError.InvalidRequest(
                $"Limit must be between 1 and {LspRequest.MaximumResultLimit}."
            );
        }

        if (request.Offset < 0)
        {
            return ToolError.InvalidRequest("Offset must not be negative.");
        }

        if (
            request.Action is LspAction.Bulk
            && request.Queries.Count is < 1 or > LspRequest.MaximumBulkQueries
        )
        {
            return ToolError.InvalidRequest(
                $"Queries must hold between 1 and {LspRequest.MaximumBulkQueries} items."
            );
        }

        if (request.Action is LspAction.Reload)
        {
            changes.Clear();
            await session.ReloadAsync(cancellationToken);
            return Status();
        }

        await session.EnsureStartedAsync(cancellationToken);
        if (!SynchronizesBeforeFirstRead(request))
        {
            await session.SynchronizeAsync(cancellationToken);
        }

        return request.Action switch
        {
            LspAction.Capabilities => new JsonResult(session.Capabilities?.DeepClone()),
            LspAction.Diagnostics => (
                await queries.DiagnosticsAsync(request, cancellationToken)
            ).AsToolResult(),
            LspAction.Symbols => await queries.SymbolsAsync(request, cancellationToken),
            LspAction.Bulk => await queries.BulkAsync(request, fileErrors, cancellationToken),
            _ when request.Action.IsNavigation => await queries.NavigationAsync(
                request,
                cancellationToken
            ),
            _ when request.Action.IsChange => await WithDiagnosticsAsync(
                await changes.ExecuteAsync(request, cancellationToken),
                cancellationToken
            ),
            LspAction.Request => await RawRequestAsync(request, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported LSP action: {request.Action}."),
        };
    }

    private SessionStatus Status() =>
        new(
            session.IsRunning,
            session.IsLoading,
            options.WorkspaceRoot,
            options.ServerPath,
            session.Progress?.Text
        );

    private async Task<Result<ToolResult>> RawRequestAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.Method))
        {
            return ToolError.InvalidRequest("Provide the LSP method for action=request.");
        }

        if (IsManagedMethod(request.Method))
        {
            return ToolError.InvalidRequest(
                "The bridge owns lifecycle and synchronization methods."
            );
        }

        if (!_readOnlyMethods.Contains(request.Method) && !request.Apply)
        {
            return ToolError.InvalidRequest(
                "This method may change the workspace. Set apply=true explicitly."
            );
        }

        if (
            request.Parameters?["textDocument"]?["uri"] is JsonValue uri
            && uri.TryGetValue<string>(out var documentUri)
            && RoslynSession.IsCSharpFile(documentUri)
        )
        {
            await session.OpenDocumentAsync(documentUri, cancellationToken);
        }

        if (request.Method is LspMethods.WorkspaceExecuteCommand)
        {
            return await changes.ExecuteCommandAsync(request.Parameters ?? [], cancellationToken);
        }

        return new JsonResult(
            await session.RequestAsync(request.Method, request.Parameters, cancellationToken)
        );
    }

    private static bool IsManagedMethod(string method)
    {
        var isLifecycle =
            method
            is LspMethods.Initialize
                or LspMethods.Initialized
                or LspMethods.Shutdown
                or LspMethods.Exit;

        var isSynchronization =
            method.StartsWith("textDocument/did", StringComparison.Ordinal)
            || method.StartsWith("workspace/did", StringComparison.Ordinal);

        return isLifecycle || isSynchronization || method is LspMethods.WorkspaceApplyEdit;
    }

    internal static CallToolResult Response(
        ToolResult? result,
        IReadOnlyList<string>? resolvedFiles = null
    )
    {
        var heading = ResultText.ResolvedFiles(resolvedFiles ?? []);
        var text =
            heading
            + ResultText.Render(result, ResultText.MaximumResponseCharacters - heading.Length);
        // A receipt must reach the client, and it drops trailing operations to fit
        return text.Length <= ResultText.MaximumResponseCharacters || result is EditReceipt
            ? Text(text, isError: false)
            : Failure(
                new ToolError(
                    ErrorCodes.ResultTooLarge,
                    $"The result exceeds {ResultText.MaximumResponseCharacters} characters. Narrow the query or lower limit."
                )
            );
    }

    /// <summary>
    /// Reports a failed application with the step that failed and the receipt of the writes that completed.
    /// </summary>
    internal static CallToolResult ApplicationFailure(
        EditApplicationException exception,
        bool cancellationRequested
    )
    {
        var cause = exception.InnerException!;
        var code = cause switch
        {
            OperationCanceledException => cancellationRequested
                ? ErrorCodes.Cancelled
                : ErrorCodes.Timeout,
            _ => ToolFailures.FailureCode(cause),
        };
        var message = cause.Message[..Math.Min(cause.Message.Length, MaximumErrorCharacters)];
        var text = new StringBuilder(ResultText.Error(new ToolError(code, message), rpcCode: null))
            .Append("\nfailed step: ")
            .Append(exception.Phase.ToString().ToLowerInvariant());
        if (exception.FailedPath is { } path)
        {
            text.Append(", path ").Append(path);
        }

        if (exception.FailedOperation is { } operation)
        {
            text.Append(", operation ").Append(operation);
        }

        text.Append('\n')
            .Append(
                ResultText.Render(
                    exception.Result,
                    ResultText.MaximumResponseCharacters - text.Length
                )
            );
        return Text(text.ToString(), isError: true);
    }

    internal static CallToolResult Failure(ToolError failure, int? rpcCode = null) =>
        Text(ResultText.Error(failure, rpcCode), isError: true);

    private static CallToolResult Text(string text, bool isError) =>
        new() { IsError = isError, Content = [new TextContentBlock { Text = text }] };

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "LSP {Action} completed in {ElapsedMilliseconds} ms"
    )]
    private static partial void OperationCompleted(
        ILogger logger,
        LspAction action,
        double elapsedMilliseconds
    );

    [LoggerMessage(Level = LogLevel.Warning, Message = "LSP {Action} failed")]
    private static partial void OperationFailed(
        ILogger logger,
        LspAction action,
        Exception exception
    );
}
