// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Frozen;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using RoslynBridge.Changes;
using RoslynBridge.Editing;
using RoslynBridge.Lsp;
using RoslynBridge.Output;
using RoslynBridge.Queries;
using RoslynBridge.Requests;
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
                return Failure(error);
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
        return Failure(new ToolError(FailureCode(exception), exception.Message));
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
            WorkspaceLoadingException => ErrorCodes.Loading,
            TimeoutException => ErrorCodes.Timeout,
            IOException or UnauthorizedAccessException or Win32Exception => ErrorCodes.Io,
            RemoteRpcException or JsonException => ErrorCodes.Lsp,
            // Only an application failure reports an unexpected cause, which is a bridge bug
            _ => ErrorCodes.Internal,
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

    // Diagnostics and a new edit synchronize Roslyn themselves, so an earlier synchronization repeats work
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

        if (request.Action is LspAction.Reload)
        {
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
            LspAction.Source => await queries.SourceAsync(request, cancellationToken),
            LspAction.Members => await queries.MembersAsync(request, cancellationToken),
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
        bool preserveApplication = false
    )
    {
        var text = ResultText.Render(
            action,
            result,
            preserveApplication,
            ResultText.MaximumResponseCharacters
        );
        return text.Length <= ResultText.MaximumResponseCharacters
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
        LspAction action,
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
            _ => FailureCode(cause),
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
                    action,
                    exception.Result,
                    application: true,
                    ResultText.MaximumResponseCharacters - text.Length
                )
            );
        return Text(text.ToString(), isError: true);
    }

    private static CallToolResult Failure(ToolError failure, int? rpcCode = null) =>
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
