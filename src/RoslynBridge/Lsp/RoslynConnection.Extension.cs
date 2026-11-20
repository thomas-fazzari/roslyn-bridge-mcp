// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.Logging;
using RoslynBridge.Lsp.Protocol;

namespace RoslynBridge.Lsp;

internal sealed partial class RoslynConnection
{
    internal const string ReferencesMessage = "RoslynBridge.Extension.ReferencesHandler";
    internal const string CallersMessage = "RoslynBridge.Extension.CallersHandler";
    internal const string ExpressionMessage = "RoslynBridge.Extension.ExpressionHandler";
    internal const string UnusedMessage = "RoslynBridge.Extension.UnusedHandler";
    internal const int RequestCancelledCode = -32800;
    internal const int ServerCancelledCode = -32802;

    private static readonly string _extensionPath = Path.Combine(
        AppContext.BaseDirectory,
        "extension",
        "RoslynBridge.Extension.dll"
    );

    private static readonly string[] _documentMessages =
    [
        ReferencesMessage,
        CallersMessage,
        ExpressionMessage,
    ];
    private static readonly string[] _workspaceMessages = [UnusedMessage];

    // A failing handler leaves the others active, and bulk_navigate dispatches concurrently
    private readonly ConcurrentDictionary<string, byte> _activeDocumentMessages = new(
        StringComparer.Ordinal
    );
    private bool _workspaceHandlersActive;

    /// <summary>
    /// Finds references inside Roslyn, without the batching delay of textDocument/references.
    /// </summary>
    public Task<Result<ExtensionReferences>> FindReferencesAsync(
        DocumentPosition position,
        CancellationToken cancellationToken
    ) =>
        DispatchWithFallbackAsync<ExtensionReferences>(
            ReferencesMessage,
            position,
            cancellationToken
        );

    /// <summary>
    /// Finds callers inside Roslyn, with the calls through base and interface members.
    /// </summary>
    public Task<Result<ExtensionCallers>> FindCallersAsync(
        DocumentPosition position,
        CancellationToken cancellationToken
    ) => DispatchWithFallbackAsync<ExtensionCallers>(CallersMessage, position, cancellationToken);

    /// <summary>
    /// Reads the compiler facts of the expression or declared name at a position. It has no LSP fallback.
    /// </summary>
    public async Task<Result<ExtensionExpression>> DescribeExpressionAsync(
        DocumentPosition position,
        CancellationToken cancellationToken
    )
    {
        var described = await DispatchDocumentMessageAsync<ExtensionExpression>(
            ExpressionMessage,
            position,
            disableOnFailure: false,
            cancellationToken
        );
        return described is ToolError error && IsCancelled(error)
            ? ToolError.InvalidLspData(
                "Roslyn cancelled the description of the expression. Retry the call."
            )
            : described;
    }

    /// <summary>
    /// Finds the declarations of some files that no live code uses.
    /// </summary>
    /// <param name="files">C# files.</param>
    public async Task<Result<UnusedReport>> FindUnusedAsync(
        IReadOnlyCollection<WorkspaceFilePath> files,
        CancellationToken cancellationToken
    )
    {
        if (!_workspaceHandlersActive)
        {
            return ToolError.InvalidLspData(
                "Listing unused symbols needs the Roslyn extension, which is unavailable in this session."
            );
        }

        // A failure of this long search says nothing about the other handlers, so they stay active
        var searched = await RequestAsync(
            LspMethods.WorkspaceDispatchExtensionMessage,
            new JsonObject
            {
                ["messageName"] = UnusedMessage,
                ["message"] = new JsonObject
                {
                    ["files"] = new JsonArray([
                        .. files.Select(file => JsonValue.Create(file.FullPath)),
                    ]),
                }.ToJsonString(),
            },
            cancellationToken
        );
        if (searched.IsError(out var error, out var result))
        {
            return error switch
            {
                _ when IsCancelled(error) => ToolError.InvalidLspData(
                    "Roslyn cancelled the search for unused symbols. Retry the call."
                ),
                { Code: ErrorCodes.Lsp } => UnusedFailure(error.Message),
                _ => error,
            };
        }

        if (ReadDispatch(result).IsError(out var invalidDispatch, out var dispatch))
            return UnusedFailure(invalidDispatch.Message);

        if (dispatch.Answer is not { } response)
            return UnusedFailure(dispatch.Error);

        if (LspJson.Parse<UnusedReport>(response).IsError(out var invalid, out var report))
            return invalid;

        var count = report.Symbols.Length;
        return report.Symbols.SelectMany(entry => entry.Via).Any(user => user < 0 || user >= count)
            ? ToolError.InvalidLspData("Roslyn returned an unused symbol index out of range.")
            : report;
    }

    // Roslyn cancels a request when the workspace changes, which says nothing about the extension
    private static bool IsCancelled(ToolError error) =>
        error.RpcCode is RequestCancelledCode or ServerCancelledCode;

    // A failed or cancelled dispatch answers not found, which sends the caller to the LSP request
    private async Task<Result<T>> DispatchWithFallbackAsync<T>(
        string messageName,
        DocumentPosition position,
        CancellationToken cancellationToken
    )
        where T : IExtensionAnswer<T>
    {
        var dispatched = await DispatchDocumentMessageAsync<T>(
            messageName,
            position,
            disableOnFailure: true,
            cancellationToken
        );
        return dispatched is ToolError { Code: ErrorCodes.Lsp } ? T.NotFound : dispatched;
    }

    /// <summary>
    /// A failure other than a cancellation disables the handler of <paramref name="messageName"/> for the session when <paramref name="disableOnFailure"/> is set.
    /// </summary>
    private async Task<Result<T>> DispatchDocumentMessageAsync<T>(
        string messageName,
        DocumentPosition position,
        bool disableOnFailure,
        CancellationToken cancellationToken
    )
        where T : IExtensionAnswer<T>
    {
        if (!_activeDocumentMessages.ContainsKey(messageName))
        {
            return ToolError.InvalidLspData(
                $"The Roslyn extension handler {messageName} is unavailable in this session."
            );
        }

        var parameters = LspParameters.At(position);
        var dispatched = await RequestAsync(
            LspMethods.TextDocumentDispatchExtensionMessage,
            new JsonObject
            {
                ["messageName"] = messageName,
                ["message"] = parameters["position"]!.ToJsonString(),
                ["textDocument"] = parameters["textDocument"]!.DeepClone(),
            },
            cancellationToken
        );
        if (dispatched.IsError(out var error, out var result))
        {
            return error is { Code: ErrorCodes.Lsp } && !IsCancelled(error)
                ? DocumentMessageFailure(messageName, error.Message, disableOnFailure)
                : error;
        }

        if (ReadDispatch(result).IsError(out var invalidDispatch, out var dispatch))
            return DocumentMessageFailure(messageName, invalidDispatch.Message, disableOnFailure);

        if (dispatch.Answer is not { } response)
            return DocumentMessageFailure(messageName, dispatch.Error, disableOnFailure);

        return LspJson.Parse<T>(response).IsError(out var invalid, out var answer)
            ? DocumentMessageFailure(messageName, invalid.Message, disableOnFailure)
            : answer;
    }

    private async Task<Result> ActivateExtensionAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_extensionPath))
        {
            LogExtensionUnavailable(logger, $"{_extensionPath} is missing");
            return Result.Success;
        }

        var activated = await RequestAsync(
            LspMethods.ServerActivateExtension,
            new JsonObject { ["assemblyFilePath"] = _extensionPath },
            cancellationToken
        );
        if (activated.IsError(out var error, out var result))
        {
            // A server without the extension handlers reports an unknown method
            if (error is not { Code: ErrorCodes.Lsp })
                return error;

            LogExtensionUnavailable(logger, error.Message);
            return Result.Success;
        }

        var read = result is null ? ExtensionActivation.None : result.Read<ExtensionActivation>();
        if (read.IsError(out var invalid, out var activation))
        {
            LogExtensionUnavailable(logger, invalid.Message);
            return Result.Success;
        }

        var unregisteredDocuments = Unregistered(
            activation.DocumentMessageHandlers,
            _documentMessages
        );
        foreach (
            var message in _documentMessages.Except(unregisteredDocuments, StringComparer.Ordinal)
        )
        {
            _activeDocumentMessages.TryAdd(message, 0);
        }

        var unregisteredWorkspace = Unregistered(
            activation.WorkspaceMessageHandlers,
            _workspaceMessages
        );
        _workspaceHandlersActive = unregisteredWorkspace.Count == 0;
        List<string> unregistered = [.. unregisteredDocuments, .. unregisteredWorkspace];
        if (unregistered.Count > 0)
        {
            LogHandlersUnregistered(
                logger,
                string.Join(", ", unregistered.Select(Consequence)),
                activation.ExtensionException?.Message ?? "Roslyn reported no error"
            );
        }

        return Result.Success;
    }

    // A broken handler would fail every request the same way, so one warning per handler is enough
    private ToolError DocumentMessageFailure(string messageName, string reason, bool disable)
    {
        if (disable && _activeDocumentMessages.TryRemove(messageName, out _))
            LogExtensionFailed(logger, messageName, reason);

        return ToolError.InvalidLspData(
            $"The Roslyn extension handler {messageName} failed: {reason}"
        );
    }

    private static ToolError UnusedFailure(string reason) =>
        ToolError.InvalidLspData($"The Roslyn extension failed to list unused symbols: {reason}");

    private static List<string> Unregistered(string[]? handlers, string[] messages) =>
        [
            .. messages.Where(message =>
                handlers?.Contains(message, StringComparer.Ordinal) is not true
            ),
        ];

    private static string Consequence(string messageName) =>
        messageName switch
        {
            ReferencesMessage => "references (uses the LSP request)",
            CallersMessage => "callers (uses the LSP request)",
            ExpressionMessage => "expression (unavailable)",
            UnusedMessage => "unused (unavailable)",
            _ => throw new ArgumentOutOfRangeException(nameof(messageName), messageName, null),
        };

    private static Result<ExtensionDispatch> ReadDispatch(JsonNode? result) =>
        result is null ? ExtensionDispatch.None : result.Read<ExtensionDispatch>();

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The Roslyn extension is unavailable, references and callers will use the LSP requests, and unused symbols and expressions cannot be listed or described: {Reason}"
    )]
    private static partial void LogExtensionUnavailable(ILogger logger, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The Roslyn extension did not register some handlers: {Handlers}. Reason: {Reason}"
    )]
    private static partial void LogHandlersUnregistered(
        ILogger logger,
        string handlers,
        string reason
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The Roslyn extension handler {Handler} failed and stays disabled until the next session: {Reason}"
    )]
    private static partial void LogExtensionFailed(ILogger logger, string handler, string reason);
}
