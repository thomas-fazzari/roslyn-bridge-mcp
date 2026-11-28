// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using RoslynBridge.Results;
using RoslynBridge.Settings;
using RoslynBridge.Telemetry;

namespace RoslynBridge.Common;

internal sealed partial class ToolPipeline(
    SettingsMonitor settings,
    WorkspacePaths paths,
    RoslynSession session,
    ILogger<ToolPipeline> logger
) : IDisposable
{
    private static readonly ToolError _timedOut = new(
        ErrorCodes.TIMEOUT,
        "The LSP operation timed out. No successful result is available."
    );

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Answers a request that the tool could not build with its error.
    /// </summary>
    public Task<CallToolResult> RunAsync<TRequest, TResult>(
        IToolHandler<TRequest, TResult> handler,
        Result<TRequest> request,
        CancellationToken cancellationToken
    )
        where TRequest : ToolRequest
        where TResult : ToolResult =>
        request switch
        {
            ToolError error => Task.FromResult(error.ToResponse()),
            TRequest valid => RunAsync(handler, valid, cancellationToken),
        };

    public async Task<CallToolResult> RunAsync<TRequest, TResult>(
        IToolHandler<TRequest, TResult> handler,
        TRequest request,
        CancellationToken cancellationToken
    )
        where TRequest : ToolRequest
        where TResult : ToolResult
    {
        if (request.Validate() is ToolError invalid)
            return invalid.ToResponse();

        var policy = request.Policy;
        // status answers while another call holds the gate
        if (!policy.Exclusive)
        {
            var answer = await handler.HandleAsync(request, cancellationToken);
            return answer.IsError(out var failure, out var value)
                ? ReportFailure(policy.Action, failure, cancellationToken)
                : value.ToResponse();
        }

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
                return fileError.ToResponse();

            var dispatched = await DispatchAsync(
                handler,
                (TRequest)resolved.Request,
                timeout.Token
            );
            if (dispatched.IsError(out var error, out var result))
                return ReportFailure(policy.Action, error, cancellationToken);

            if (logger.IsEnabled(LogLevel.Debug))
            {
                var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                OperationCompleted(logger, policy.Action, elapsedMilliseconds);
            }

            return result.ToResponse(resolved.Files);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return _timedOut.ToResponse();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private CallToolResult ReportFailure(
        LspAction action,
        ToolError error,
        CancellationToken cancellationToken
    )
    {
        // A failed edit wrote files, so a cancellation still reports its receipt
        if (error is EditFailure failure)
        {
            OperationFailed(logger, action, failure.Code, failure.Message);
            return
                failure.Code is ErrorCodes.CANCELLED && !cancellationToken.IsCancellationRequested
                ? (failure with { Code = ErrorCodes.TIMEOUT }).ToResponse()
                : failure.ToResponse();
        }

        // A cancellation without written files ends the call as an exception would
        if (error.Code is ErrorCodes.CANCELLED)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _timedOut.ToResponse();
        }

        if (error.IsInfrastructureFailure)
            OperationFailed(logger, action, error.Code, error.Message);

        return error.ToResponse();
    }

    private async Task<Result<TResult>> DispatchAsync<TRequest, TResult>(
        IToolHandler<TRequest, TResult> handler,
        TRequest request,
        CancellationToken cancellationToken
    )
        where TRequest : ToolRequest
        where TResult : ToolResult
    {
        var prepared = await PrepareServerAsync(request.Policy.Server, cancellationToken);
        return prepared is ToolError prepareError
            ? prepareError
            : await handler.HandleAsync(request, cancellationToken);
    }

    private async Task<Result> PrepareServerAsync(
        ServerPreparation preparation,
        CancellationToken cancellationToken
    )
    {
        if (preparation is ServerPreparation.None)
            return Result.Success;

        if (await session.EnsureStartedAsync(cancellationToken) is ToolError startError)
            return startError;

        return preparation is ServerPreparation.StartAndSynchronize
            ? await session.SynchronizeAsync(cancellationToken)
            : Result.Success;
    }

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
