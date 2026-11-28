// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Nerdbank.Streams;
using RoslynBridge.Lsp;
using RoslynBridge.Lsp.Protocol;
using StreamJsonRpc;

namespace RoslynBridge.Tests.Fakes;

internal sealed class FakeRoslynServer : IRoslynServer
{
    private readonly Stream _serverSide;
    private readonly Stream _errorWriter;
    private readonly SystemTextJsonFormatter _formatter = new();
    private readonly JsonRpc _rpc;
    private readonly TaskCompletionSource _exited = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly ConcurrentQueue<(string Method, JsonNode? Parameters)> _notifications = new();

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "JsonRpc owns and disposes the message handler."
    )]
    public FakeRoslynServer()
    {
        (Input, _serverSide) = FullDuplexStream.CreatePair();
        var (errorReader, errorWriter) = FullDuplexStream.CreatePair();
        _errorWriter = errorWriter;
        Error = new StreamReader(errorReader, Encoding.UTF8);
        _rpc = new JsonRpc(new HeaderDelimitedMessageHandler(_serverSide, _serverSide, _formatter));
        _rpc.AddLocalRpcTarget(new Target(this), options: null);
        _rpc.StartListening();
    }

    /// <summary>
    /// A references or project contexts request that the bridge cancels ends without waiting for its answer.
    /// The default handler returns null.
    /// </summary>
    public Func<string, JsonNode?, Task<JsonNode?>> Handle { get; set; } =
        (_, _) => Task.FromResult<JsonNode?>(null);

    /// <summary>
    /// Completes when the fake reports the project load. Defaults to at once.
    /// </summary>
    public Task ProjectLoad { get; set; } = Task.CompletedTask;

    /// <summary>
    /// The capabilities of the initialize answer. Defaults to none.
    /// </summary>
    public JsonObject Capabilities { get; set; } = [];

    /// <summary>
    /// The hover contents at every position. Defaults to a plain string, so every position has a symbol.
    /// </summary>
    public JsonNode HoverContents { get; set; } = "symbol";

    /// <summary>
    /// Runs before the fake answers the shutdown request. Defaults to nothing.
    /// </summary>
    public Func<Task> OnShutdown { get; set; } = () => Task.CompletedTask;

    /// <summary>
    /// Only didOpen, didChange, didClose, didChangeWatchedFiles, solution/open and project/open, in arrival order.
    /// </summary>
    public IReadOnlyCollection<(string Method, JsonNode? Parameters)> Notifications =>
        _notifications;

    public Stream Input { get; }

    public Stream Output => Input;

    public StreamReader Error { get; }

    public bool HasExited { get; private set; }

    public async Task WriteErrorAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _errorWriter.WriteAsync(bytes, cancellationToken);
        await _errorWriter.FlushAsync(cancellationToken);
    }

    public Task<JsonNode?> RequestClientAsync(
        string method,
        JsonNode parameters,
        CancellationToken cancellationToken
    ) => _rpc.InvokeWithParameterObjectAsync<JsonNode?>(method, parameters, cancellationToken);

    /// <summary>
    /// Returns after the bridge answers a request sent after the notification, so it has handled it.
    /// </summary>
    public Task ReportProgressAsync(
        string token,
        JsonObject value,
        CancellationToken cancellationToken
    ) =>
        NotifyClientAsync(
            LspMethods.PROGRESS,
            new JsonObject { ["token"] = token, ["value"] = value },
            cancellationToken
        );

    /// <summary>
    /// Sends window/logMessage. Returns after the bridge answers a request sent after it, so it has handled it.
    /// </summary>
    public Task LogMessageAsync(string message, CancellationToken cancellationToken) =>
        NotifyClientAsync(
            LspMethods.WINDOW_LOG_MESSAGE,
            new JsonObject { ["type"] = 1, ["message"] = message },
            cancellationToken
        );

    public void Crash()
    {
        if (HasExited)
            return;

        HasExited = true;
        _rpc.Dispose();
        _formatter.Dispose();
        _serverSide.Dispose();
        _errorWriter.Dispose();
        _exited.TrySetResult();
    }

    public void Kill() => Crash();

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _exited.Task.WaitAsync(cancellationToken);

    public void Dispose()
    {
        Crash();
        Input.Dispose();
        Error.Dispose();
    }

    private async Task NotifyClientAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken
    )
    {
        await _rpc.NotifyWithParameterObjectAsync(method, parameters);
        await _rpc.InvokeWithCancellationAsync<JsonNode?>(
            LspMethods.WORKSPACE_WORKSPACE_FOLDERS,
            cancellationToken: cancellationToken
        );
    }

    private sealed class Target(FakeRoslynServer server)
    {
        [JsonRpcMethod(LspMethods.INITIALIZE, UseSingleObjectParameterDeserialization = true)]
        public JsonObject Initialize(JsonObject parameters)
        {
            _ = parameters;
            return new JsonObject { ["capabilities"] = server.Capabilities.DeepClone() };
        }

        // Roslyn reports the project load after the handshake
        [JsonRpcMethod(LspMethods.INITIALIZED, UseSingleObjectParameterDeserialization = true)]
        public async Task InitializedAsync(JsonObject parameters)
        {
            _ = parameters;
            await server.ProjectLoad;
            await server._rpc.NotifyAsync(LspMethods.WORKSPACE_PROJECT_INITIALIZATION_COMPLETE);
        }

        [JsonRpcMethod(LspMethods.SOLUTION_OPEN, UseSingleObjectParameterDeserialization = true)]
        public void SolutionOpen(JsonNode? parameters) =>
            server._notifications.Enqueue((LspMethods.SOLUTION_OPEN, parameters));

        [JsonRpcMethod(LspMethods.PROJECT_OPEN, UseSingleObjectParameterDeserialization = true)]
        public void ProjectOpen(JsonNode? parameters) =>
            server._notifications.Enqueue((LspMethods.PROJECT_OPEN, parameters));

        [JsonRpcMethod(
            LspMethods.TEXT_DOCUMENT_DID_CLOSE,
            UseSingleObjectParameterDeserialization = true
        )]
        public void DidClose(JsonNode? parameters) =>
            server._notifications.Enqueue((LspMethods.TEXT_DOCUMENT_DID_CLOSE, parameters));

        [JsonRpcMethod(
            LspMethods.WORKSPACE_DID_CHANGE_WATCHED_FILES,
            UseSingleObjectParameterDeserialization = true
        )]
        public void DidChangeWatchedFiles(JsonNode? parameters) =>
            server._notifications.Enqueue(
                (LspMethods.WORKSPACE_DID_CHANGE_WATCHED_FILES, parameters)
            );

        [JsonRpcMethod(
            LspMethods.TEXT_DOCUMENT_DID_OPEN,
            UseSingleObjectParameterDeserialization = true
        )]
        public void DidOpen(JsonNode? parameters) =>
            server._notifications.Enqueue((LspMethods.TEXT_DOCUMENT_DID_OPEN, parameters));

        [JsonRpcMethod(
            LspMethods.TEXT_DOCUMENT_DID_CHANGE,
            UseSingleObjectParameterDeserialization = true
        )]
        public void DidChange(JsonNode? parameters) =>
            server._notifications.Enqueue((LspMethods.TEXT_DOCUMENT_DID_CHANGE, parameters));

        // Roslyn answers an object, so a handler that ignores the method leaves the extension inactive
        [JsonRpcMethod(
            LspMethods.SERVER_ACTIVATE_EXTENSION,
            UseSingleObjectParameterDeserialization = true
        )]
        public async Task<JsonNode?> ActivateExtensionAsync(JsonNode? parameters) =>
            await server.Handle(LspMethods.SERVER_ACTIVATE_EXTENSION, parameters) as JsonObject;

        [JsonRpcMethod(
            LspMethods.TEXT_DOCUMENT_DISPATCH_EXTENSION_MESSAGE,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> DispatchExtensionMessageAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TEXT_DOCUMENT_DISPATCH_EXTENSION_MESSAGE, parameters);

        [JsonRpcMethod(
            LspMethods.TEXT_DOCUMENT_RENAME,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> RenameAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TEXT_DOCUMENT_RENAME, parameters);

        [JsonRpcMethod(
            LspMethods.WORKSPACE_WILL_RENAME_FILES,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> WillRenameFilesAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.WORKSPACE_WILL_RENAME_FILES, parameters);

        [JsonRpcMethod(LspMethods.WORKSPACE_SYMBOL, UseSingleObjectParameterDeserialization = true)]
        public Task<JsonNode?> WorkspaceSymbolAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.WORKSPACE_SYMBOL, parameters);

        [JsonRpcMethod(
            LspMethods.TEXT_DOCUMENT_DOCUMENT_SYMBOL,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> DocumentSymbolAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TEXT_DOCUMENT_DOCUMENT_SYMBOL, parameters);

        [JsonRpcMethod(
            LspMethods.TEXT_DOCUMENT_DEFINITION,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> DefinitionAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TEXT_DOCUMENT_DEFINITION, parameters);

        [JsonRpcMethod(
            LspMethods.TEXT_DOCUMENT_IMPLEMENTATION,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> ImplementationAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TEXT_DOCUMENT_IMPLEMENTATION, parameters);

        [JsonRpcMethod(
            LspMethods.TEXT_DOCUMENT_REFERENCES,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> ReferencesAsync(
            JsonNode? parameters,
            CancellationToken cancellationToken
        ) =>
            server
                .Handle(LspMethods.TEXT_DOCUMENT_REFERENCES, parameters)
                .WaitAsync(cancellationToken);

        [JsonRpcMethod(
            LspMethods.TEXT_DOCUMENT_HOVER,
            UseSingleObjectParameterDeserialization = true
        )]
        public JsonObject Hover(JsonNode? parameters)
        {
            _ = parameters;
            return new JsonObject { ["contents"] = server.HoverContents.DeepClone() };
        }

        [JsonRpcMethod(
            LspMethods.TEXT_DOCUMENT_DOCUMENT_HIGHLIGHT,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> DocumentHighlightAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TEXT_DOCUMENT_DOCUMENT_HIGHLIGHT, parameters);

        [JsonRpcMethod(
            LspMethods.TEXT_DOCUMENT_DIAGNOSTIC,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> DiagnosticAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TEXT_DOCUMENT_DIAGNOSTIC, parameters);

        [JsonRpcMethod(
            LspMethods.TEXT_DOCUMENT_GET_PROJECT_CONTEXTS,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> ProjectContextsAsync(
            JsonNode? parameters,
            CancellationToken cancellationToken
        ) =>
            server
                .Handle(LspMethods.TEXT_DOCUMENT_GET_PROJECT_CONTEXTS, parameters)
                .WaitAsync(cancellationToken);

        [JsonRpcMethod(
            LspMethods.TEXT_DOCUMENT_CODE_ACTION,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> CodeActionAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TEXT_DOCUMENT_CODE_ACTION, parameters);

        [JsonRpcMethod(
            LspMethods.TEXT_DOCUMENT_PREPARE_CALL_HIERARCHY,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> PrepareCallHierarchyAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TEXT_DOCUMENT_PREPARE_CALL_HIERARCHY, parameters);

        [JsonRpcMethod(
            LspMethods.CALL_HIERARCHY_INCOMING_CALLS,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> IncomingCallsAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.CALL_HIERARCHY_INCOMING_CALLS, parameters);

        [JsonRpcMethod(
            LspMethods.CALL_HIERARCHY_OUTGOING_CALLS,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> OutgoingCallsAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.CALL_HIERARCHY_OUTGOING_CALLS, parameters);

        [JsonRpcMethod(
            LspMethods.TEXT_DOCUMENT_PREPARE_TYPE_HIERARCHY,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> PrepareTypeHierarchyAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TEXT_DOCUMENT_PREPARE_TYPE_HIERARCHY, parameters);

        [JsonRpcMethod(
            LspMethods.TYPE_HIERARCHY_SUPERTYPES,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> SupertypesAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TYPE_HIERARCHY_SUPERTYPES, parameters);

        [JsonRpcMethod(
            LspMethods.TYPE_HIERARCHY_SUBTYPES,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> SubtypesAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TYPE_HIERARCHY_SUBTYPES, parameters);

        [JsonRpcMethod(
            LspMethods.CODE_ACTION_RESOLVE_FIX_ALL,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> ResolveFixAllAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.CODE_ACTION_RESOLVE_FIX_ALL, parameters);

        [JsonRpcMethod(
            LspMethods.WORKSPACE_EXECUTE_COMMAND,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> WorkspaceExecuteCommandAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.WORKSPACE_EXECUTE_COMMAND, parameters);

        [JsonRpcMethod(LspMethods.SHUTDOWN)]
        public Task ShutdownAsync() => server.OnShutdown();

        [JsonRpcMethod(LspMethods.EXIT)]
        public void Exit() => server.Crash();
    }
}
