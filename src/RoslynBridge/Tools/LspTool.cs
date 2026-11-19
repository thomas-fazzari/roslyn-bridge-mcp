// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using RoslynBridge.Changes;
using RoslynBridge.Editing;
using RoslynBridge.Lsp;
using RoslynBridge.Output;
using RoslynBridge.Queries;
using RoslynBridge.Requests;
using RoslynBridge.Results;
using RoslynBridge.Settings;
using RoslynBridge.Telemetry;

namespace RoslynBridge.Tools;

internal sealed partial class LspTool(
    SettingsMonitor settings,
    WorkspacePaths paths,
    RoslynSession session,
    LspQueries queries,
    LspChanges changes,
    ILogger<LspTool> logger
) : IDisposable
{
    private const int MaximumErrorCharacters = 2_000;
    private const int MaximumCheckedFiles = 128;

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

    private static readonly ToolError _timedOut = new(
        ErrorCodes.Timeout,
        "The LSP operation timed out. No successful result is available."
    );

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<CallToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken
    )
    {
        // status answers while another call holds the gate
        if (request is StatusRequest)
            return Response(Status());

        var policy = request.Policy;
        var queued = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(cancellationToken);
        var started = Stopwatch.GetTimestamp();
        BridgeTelemetry.QueueDuration.Record(
            Stopwatch.GetElapsedTime(queued, started).TotalSeconds
        );
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(
                session.IsReady && !policy.MayRestart
                    ? settings.CurrentValue.RequestTimeout
                    : settings.CurrentValue.StartupTimeout + settings.CurrentValue.RequestTimeout
            );
            var resolution = await ResolveFilesAsync(request, timeout.Token);
            if (resolution.IsError(out var fileError, out var resolved))
                return Failure(fileError);

            var dispatched = await DispatchAsync(
                resolved.Request,
                resolved.FileErrors,
                timeout.Token
            );
            if (dispatched.IsError(out var error, out var result))
                return ReportFailure(policy.Action, error, cancellationToken);

            if (logger.IsEnabled(LogLevel.Debug))
            {
                var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                OperationCompleted(logger, policy.Action, elapsedMilliseconds);
            }

            return Response(result, resolved.Files);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(_timedOut);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    internal static CallToolResult Response(
        ToolResult result,
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

    internal static CallToolResult Failure(ToolError failure) =>
        Text(ResultText.Error(failure), isError: true);

    private static CallToolResult ApplicationFailure(
        EditFailure failure,
        bool cancellationRequested
    )
    {
        var code =
            failure.Code is ErrorCodes.Cancelled
                ? CancellationCode(cancellationRequested)
                : failure.Code;
        var message = failure.Message[..Math.Min(failure.Message.Length, MaximumErrorCharacters)];
        var text = new StringBuilder(ResultText.Error(new ToolError(code, message)))
            .Append("\nfailed step: ")
            .Append(failure.Phase.ToString().ToLowerInvariant());
        if (failure.FailedPath is { } path)
            text.Append(", path ").Append(path);

        if (failure.FailedOperation is { } operation)
            text.Append(", operation ").Append(operation);

        text.Append('\n')
            .Append(
                ResultText.Render(
                    failure.Receipt,
                    ResultText.MaximumResponseCharacters - text.Length
                )
            );
        return Text(text.ToString(), isError: true);
    }

    private static string CancellationCode(bool cancellationRequested) =>
        cancellationRequested ? ErrorCodes.Cancelled : ErrorCodes.Timeout;

    private CallToolResult ReportFailure(
        LspAction action,
        ToolError error,
        CancellationToken cancellationToken
    )
    {
        if (error is EditFailure failure)
        {
            // Files changed outside the bridge's synchronization, so Roslyn starts again from disk
            changes.Clear();
            session.RequireReload();
            OperationFailed(logger, action, failure.Code, failure.Message);
            return ApplicationFailure(failure, cancellationToken.IsCancellationRequested);
        }

        // A cancellation without written files ends the call as an exception would
        if (error.Code is ErrorCodes.Cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure(_timedOut);
        }

        if (error.IsInfrastructureFailure)
            OperationFailed(logger, action, error.Code, error.Message);

        return Failure(error);
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
            OperationFailed(logger, LspAction.Diagnostics, error.Code, error.Message);
            return receipt;
        }

        return receipt with
        {
            Diagnostics = report,
        };
    }

    private async Task<Result<ToolResult>> DispatchAsync(
        ToolRequest request,
        IReadOnlyDictionary<string, ToolError> fileErrors,
        CancellationToken cancellationToken
    )
    {
        if (request.Page.Limit is < 1 or > Paging.MaximumLimit)
            return ToolError.InvalidRequest($"Limit must be between 1 and {Paging.MaximumLimit}.");

        if (request.Page.Offset < 0)
            return ToolError.InvalidRequest("Offset must not be negative.");

        if (request is BulkRequest { Queries.Count: < 1 or > BulkRequest.MaximumQueries })
        {
            return ToolError.InvalidRequest(
                $"Queries must hold between 1 and {BulkRequest.MaximumQueries} items."
            );
        }

        var policy = request.Policy;
        if (policy.StartsServer)
        {
            if (await session.EnsureStartedAsync(cancellationToken) is ToolError startError)
                return startError;

            if (
                !policy.SynchronizesItself
                && await session.SynchronizeAsync(cancellationToken) is ToolError syncError
            )
            {
                return syncError;
            }
        }

        return request switch
        {
            StatusRequest => Status(),
            ReloadRequest => await ReloadAsync(cancellationToken),
            CapabilitiesRequest => new JsonResult(session.Capabilities?.DeepClone()),
            DiagnosticsRequest diagnostics => (
                await queries.DiagnosticsAsync(diagnostics, cancellationToken)
            ).AsToolResult(),
            SymbolsRequest { Unused: true } symbols => await queries.UnusedAsync(
                symbols,
                cancellationToken
            ),
            SymbolsRequest symbols => (
                await queries.SymbolsAsync(symbols, cancellationToken)
            ).AsToolResult(),
            BulkRequest bulk => await queries.BulkAsync(bulk, fileErrors, cancellationToken),
            NavigationRequest navigation => await queries.NavigationAsync(
                navigation,
                cancellationToken
            ),
            EditRequest edit => await WithDiagnosticsAsync(
                await changes.ExecuteAsync(edit, cancellationToken),
                cancellationToken
            ),
            RawLspRequest raw => await RawRequestAsync(raw, cancellationToken),
        };
    }

    private async Task<Result<ToolResult>> ReloadAsync(CancellationToken cancellationToken)
    {
        BridgeOptions reloaded;
        try
        {
            reloaded = settings.Reload();
        }
        catch (InvalidSettingsException exception)
        {
            return new ToolError(ErrorCodes.InvalidSettings, exception.Message);
        }

        changes.Clear();
        paths.Exclude(reloaded.Exclude);
        var restarted = await session.ReloadAsync(cancellationToken);
        return restarted is ToolError error ? error : Status();
    }

    private SessionStatus Status() =>
        new(
            session.IsRunning,
            session.IsLoading,
            settings.CurrentValue.WorkspaceRoot,
            settings.CurrentValue.ServerPath,
            session.Progress?.Text
        );

    private async Task<Result<ToolResult>> RawRequestAsync(
        RawLspRequest request,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.Method))
            return ToolError.InvalidRequest("Provide the LSP method for action=request.");

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
            && WorkspacePaths.IsCSharpFile(documentUri)
            && (await session.OpenDocumentAsync(documentUri, cancellationToken)).IsError(
                out var error,
                out _
            )
        )
        {
            return error;
        }

        if (request.Method is LspMethods.WorkspaceExecuteCommand)
        {
            return (
                await changes.ExecuteCommandAsync(request.Parameters ?? [], cancellationToken)
            ).AsToolResult();
        }

        return (
            await session.RequestAsync(request.Method, request.Parameters, cancellationToken)
        ).Map<ToolResult>(answer => new JsonResult(answer));
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "LSP {Action} failed with {Code}: {Reason}")]
    private static partial void OperationFailed(
        ILogger logger,
        LspAction action,
        string code,
        string reason
    );
}
