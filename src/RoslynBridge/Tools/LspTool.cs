// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Frozen;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using RoslynBridge.Editing;
using RoslynBridge.Lsp;
using StreamJsonRpc;

namespace RoslynBridge.Tools;

internal sealed partial class LspTool(
    BridgeOptions options,
    RoslynSession session,
    LspQueries queries,
    LspChanges changes,
    ILogger<LspTool> logger
) : IDisposable
{
    internal const string InvalidRequestErrorCode = "invalid_request";
    internal const string StaleEditErrorCode = "stale_edit";
    internal const string LoadingErrorCode = "loading";
    internal const string TimeoutErrorCode = "timeout";
    internal const string CancelledErrorCode = "cancelled";
    internal const string LspErrorCode = "lsp_error";
    internal const string IoErrorCode = "io_error";
    internal const string ResultTooLargeErrorCode = "result_too_large";
    internal const string InternalErrorCode = "internal_error";

    internal const int MaximumResponseCharacters = 32_000;

    // Leaves room for the action and result wrapper around a preview
    internal const int PreviewBudgetCharacters = MaximumResponseCharacters - 100;
    private const int MaximumErrorCharacters = 2_000;

    private static readonly FrozenSet<string> _readOnlyMethods = new[]
    {
        LspMethods.TextDocumentDefinition,
        LspMethods.TextDocumentTypeDefinition,
        LspMethods.TextDocumentImplementation,
        LspMethods.TextDocumentReferences,
        LspMethods.TextDocumentHover,
        LspMethods.TextDocumentDocumentSymbol,
        LspMethods.TextDocumentCompletion,
        LspMethods.CompletionItemResolve,
        LspMethods.TextDocumentSignatureHelp,
        LspMethods.TextDocumentCodeAction,
        LspMethods.CodeActionResolve,
        LspMethods.TextDocumentPrepareRename,
        LspMethods.TextDocumentRename,
        LspMethods.TextDocumentDiagnostic,
        LspMethods.TextDocumentGetProjectContexts,
        LspMethods.WorkspaceDiagnostic,
        LspMethods.WorkspaceSymbol,
        LspMethods.WorkspaceSymbolResolve,
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
            return Response(request.Action, Status());
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
            var result = await DispatchAsync(request, timeout.Token);
            if (result.Error is { } error)
            {
                return Failure(request.Action, error);
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                OperationCompleted(logger, request.Action, elapsedMilliseconds);
            }

            return Response(
                request.Action,
                result.Value,
                preserveApplication: IsApplicationRequest(request)
            );
        }
        catch (EditApplicationException exception)
        {
            return ReportApplicationFailure(request.Action, exception, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                request.Action,
                new ToolError(
                    TimeoutErrorCode,
                    "The LSP operation timed out. No successful result is available."
                )
            );
        }
        catch (RemoteInvocationException exception)
        {
            return Failure(
                request.Action,
                new ToolError(LspErrorCode, exception.Message),
                exception.ErrorCode
            );
        }
        catch (Exception exception) when (IsReported(exception))
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
        return Failure(action, new ToolError(FailureCode(exception), exception.Message));
    }

    // Expected failures mapped as structured errors (anything else is a bridge bug)
    private static bool IsReported(Exception exception) =>
        exception
            is ToolErrorException
                or WorkspaceLoadingException
                or IOException
                or UnauthorizedAccessException
                or Win32Exception
                or RemoteRpcException
                or TimeoutException
                or JsonException;

    internal static string FailureCode(Exception exception) =>
        exception switch
        {
            ToolErrorException toolError => toolError.Error.Code,
            WorkspaceLoadingException => LoadingErrorCode,
            TimeoutException => TimeoutErrorCode,
            IOException or UnauthorizedAccessException or Win32Exception => IoErrorCode,
            RemoteRpcException or JsonException => LspErrorCode,
            // Only an application failure reports an unexpected cause, which is a bridge bug
            _ => InternalErrorCode,
        };

    private CallToolResult ReportApplicationFailure(
        LspAction action,
        EditApplicationException exception,
        CancellationToken cancellationToken
    )
    {
        changes.Clear();
        session.RequireReload();
        OperationFailed(logger, action, exception);
        return ApplicationFailure(action, exception, cancellationToken.IsCancellationRequested);
    }

    private static bool MayRestartServer(LspRequest request) =>
        request.Action is LspAction.Reload || request.Apply;

    // Diagnostics first announces new files, and a new edit captures its snapshot first
    // Both synchronize Roslyn then, so an earlier synchronization would repeat the work
    private static bool SynchronizesBeforeFirstRead(LspRequest request) =>
        request.Action is LspAction.Diagnostics
        || (
            request.ProposalId is null
            && request.Action is LspAction.Rename or LspAction.RenameFile or LspAction.CodeActions
        );

    private static bool IsApplicationRequest(LspRequest request) =>
        request.Apply
        && (
            request.Action switch
            {
                LspAction.Rename or LspAction.RenameFile or LspAction.CodeActions => true,
                LspAction.Request => request.Method is LspMethods.WorkspaceExecuteCommand,
                _ => false,
            }
        );

    private async Task<Result<JsonNode?>> DispatchAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.Limit is < 1 or > LspRequest.MaximumResultLimit)
        {
            return ToolError.InvalidRequest(
                $"Limit must be between 1 and {LspRequest.MaximumResultLimit}."
            );
        }

        switch (request.Action)
        {
            case LspAction.Reload:
                changes.Clear();
                await session.ReloadAsync(cancellationToken);
                return new JsonObject { ["running"] = true };
        }

        await session.EnsureStartedAsync(cancellationToken);
        if (!SynchronizesBeforeFirstRead(request))
        {
            await session.SynchronizeAsync(cancellationToken);
        }

        return request.Action switch
        {
            LspAction.Capabilities => session.Capabilities?.DeepClone(),
            LspAction.Diagnostics => await queries.DiagnosticsAsync(request, cancellationToken),
            LspAction.Symbols => await queries.SymbolsAsync(request, cancellationToken),
            LspAction.Definition
            or LspAction.TypeDefinition
            or LspAction.Implementation
            or LspAction.References
            or LspAction.Hover
            or LspAction.Callers
            or LspAction.Callees
            or LspAction.Supertypes
            or LspAction.Subtypes => await queries.NavigationAsync(request, cancellationToken),
            LspAction.Rename or LspAction.RenameFile or LspAction.CodeActions =>
                await changes.ExecuteAsync(request, cancellationToken),
            LspAction.Request => await RawRequestAsync(request, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported LSP action: {request.Action}."),
        };
    }

    private JsonObject Status() =>
        new()
        {
            ["running"] = session.IsRunning,
            ["loading"] = session.IsLoading,
            ["workspace"] = options.WorkspaceRoot,
            ["server"] = options.ServerPath,
        };

    private async Task<Result<JsonNode?>> RawRequestAsync(
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
        )
        {
            await session.OpenDocumentAsync(documentUri, cancellationToken);
        }

        if (request.Method is LspMethods.WorkspaceExecuteCommand)
        {
            return await changes.ExecuteCommandAsync(request.Parameters ?? [], cancellationToken);
        }

        return await session.RequestAsync(request.Method, request.Parameters, cancellationToken);
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
        LspAction action,
        JsonNode? result,
        JsonObject? error = null,
        bool preserveApplication = false
    )
    {
        var payload = new JsonObject
        {
            ["action"] = JsonSerializer.SerializeToNode(
                action,
                BridgeJsonContext.Default.LspAction
            ),
        };
        if (error is not null)
        {
            payload["error"] = error;
        }

        if (result is not null || error is null)
        {
            payload["result"] = result;
        }

        var element = JsonSerializer.SerializeToElement(
            payload,
            BridgeJsonContext.Default.JsonObject
        );
        if (element.GetRawText().Length > MaximumResponseCharacters)
        {
            if (!preserveApplication || result is not JsonObject application)
            {
                return Failure(
                    action,
                    new ToolError(
                        ResultTooLargeErrorCode,
                        $"The result exceeds {MaximumResponseCharacters} characters. Narrow the query or lower limit."
                    )
                );
            }

            CompactApplication(payload, application);
            element = JsonSerializer.SerializeToElement(
                payload,
                BridgeJsonContext.Default.JsonObject
            );
        }

        return new CallToolResult
        {
            IsError = error is not null,
            StructuredContent = element,
            Content =
            [
                // Some clients show the model only the text, so it carries the structured result
                new TextContentBlock { Text = element.GetRawText() },
            ],
        };
    }

    private static void CompactApplication(JsonObject payload, JsonObject result)
    {
        if (result.Remove("commandResult"))
        {
            result["commandResultTruncated"] = true;
        }

        if (
            payload.ToJsonString(BridgeJsonContext.Default.Options).Length
            <= MaximumResponseCharacters
        )
        {
            return;
        }

        var files = result["files"]!.AsArray();
        result.Remove("files");
        var included = new JsonArray();
        result["files"] = included;
        result["filesTruncated"] = true;
        EditPreview.KeepLeadingFiles(
            files,
            included,
            MaximumResponseCharacters
                - payload.ToJsonString(BridgeJsonContext.Default.Options).Length
        );
    }

    internal static CallToolResult ApplicationFailure(
        LspAction action,
        EditApplicationException exception,
        bool cancellationRequested
    )
    {
        var cause = exception.InnerException!;
        var code = cause switch
        {
            OperationCanceledException => cancellationRequested
                ? CancelledErrorCode
                : TimeoutErrorCode,
            _ => FailureCode(cause),
        };
        return Response(
            action,
            exception.Result,
            new JsonObject
            {
                ["code"] = code,
                ["message"] = cause.Message[
                    ..Math.Min(cause.Message.Length, MaximumErrorCharacters)
                ],
                ["phase"] = JsonSerializer.SerializeToNode(
                    exception.Phase,
                    BridgeJsonContext.Default.EditApplicationPhase
                ),
                ["path"] = exception.FailedPath,
                ["operation"] = exception.FailedOperation,
            },
            preserveApplication: true
        );
    }

    private static CallToolResult Failure(LspAction action, ToolError failure, int? rpcCode = null)
    {
        var error = new JsonObject
        {
            ["code"] = failure.Code,
            ["message"] = failure.Message,
            ["rpcCode"] = rpcCode,
        };
        if (failure.Candidates is { Count: > 0 } candidates)
        {
            error["candidates"] = candidates;
        }

        return Response(action, result: null, error);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "LSP {Action} failed")]
    private static partial void OperationFailed(
        ILogger logger,
        LspAction action,
        Exception exception
    );
}
