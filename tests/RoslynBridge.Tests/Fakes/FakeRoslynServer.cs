// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Nerdbank.Streams;
using RoslynBridge.Lsp;
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
    /// Answers workspace/symbol, document symbol, definition, references, document highlight, diagnostic, project context,
    /// hierarchy, code action and workspace/executeCommand requests, given the method and its parameters.
    /// A references request that the bridge cancels ends without waiting for its answer.
    /// The default handler returns null.
    /// </summary>
    public Func<string, JsonNode?, Task<JsonNode?>> Handle { get; set; } =
        (_, _) => Task.FromResult<JsonNode?>(null);

    /// <summary>
    /// Completes when the fake reports the project load, which defaults to at once.
    /// </summary>
    public Task ProjectLoad { get; set; } = Task.CompletedTask;

    /// <summary>
    /// The textDocument/didOpen, textDocument/didChange, solution/open and project/open notifications received, in arrival order.
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

    /// <summary>Sends a request to the bridge, as Roslyn does with workspace/applyEdit.</summary>
    public Task<JsonNode?> RequestClientAsync(
        string method,
        JsonNode parameters,
        CancellationToken cancellationToken
    ) => _rpc.InvokeWithParameterObjectAsync<JsonNode?>(method, parameters, cancellationToken);

    /// <summary>
    /// Sends a $/progress notification, then waits for the bridge to answer a request sent after it,
    /// which shows that the bridge handled the notification.
    /// </summary>
    public async Task ReportProgressAsync(
        string token,
        JsonObject value,
        CancellationToken cancellationToken
    )
    {
        await _rpc.NotifyWithParameterObjectAsync(
            LspMethods.Progress,
            new JsonObject { ["token"] = token, ["value"] = value }
        );
        await _rpc.InvokeWithCancellationAsync<JsonNode?>(
            LspMethods.WorkspaceWorkspaceFolders,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>Ends the process abruptly, as a crash would.</summary>
    public void Crash()
    {
        if (HasExited)
        {
            return;
        }

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

    private sealed class Target(FakeRoslynServer server)
    {
        [JsonRpcMethod(LspMethods.Initialize, UseSingleObjectParameterDeserialization = true)]
        public static JsonObject Initialize(JsonObject parameters)
        {
            _ = parameters;
            return new JsonObject { ["capabilities"] = new JsonObject() };
        }

        // Roslyn reports the project load after the handshake
        [JsonRpcMethod(LspMethods.Initialized, UseSingleObjectParameterDeserialization = true)]
        public async Task InitializedAsync(JsonObject parameters)
        {
            _ = parameters;
            await server.ProjectLoad;
            await server._rpc.NotifyAsync(LspMethods.WorkspaceProjectInitializationComplete);
        }

        [JsonRpcMethod(LspMethods.SolutionOpen, UseSingleObjectParameterDeserialization = true)]
        public void SolutionOpen(JsonNode? parameters) =>
            server._notifications.Enqueue((LspMethods.SolutionOpen, parameters));

        [JsonRpcMethod(LspMethods.ProjectOpen, UseSingleObjectParameterDeserialization = true)]
        public void ProjectOpen(JsonNode? parameters) =>
            server._notifications.Enqueue((LspMethods.ProjectOpen, parameters));

        [JsonRpcMethod(
            LspMethods.TextDocumentDidOpen,
            UseSingleObjectParameterDeserialization = true
        )]
        public void DidOpen(JsonNode? parameters) =>
            server._notifications.Enqueue((LspMethods.TextDocumentDidOpen, parameters));

        [JsonRpcMethod(
            LspMethods.TextDocumentDidChange,
            UseSingleObjectParameterDeserialization = true
        )]
        public void DidChange(JsonNode? parameters) =>
            server._notifications.Enqueue((LspMethods.TextDocumentDidChange, parameters));

        [JsonRpcMethod(LspMethods.WorkspaceSymbol, UseSingleObjectParameterDeserialization = true)]
        public Task<JsonNode?> WorkspaceSymbolAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.WorkspaceSymbol, parameters);

        [JsonRpcMethod(
            LspMethods.TextDocumentDocumentSymbol,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> DocumentSymbolAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TextDocumentDocumentSymbol, parameters);

        [JsonRpcMethod(
            LspMethods.TextDocumentDefinition,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> DefinitionAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TextDocumentDefinition, parameters);

        [JsonRpcMethod(
            LspMethods.TextDocumentReferences,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> ReferencesAsync(
            JsonNode? parameters,
            CancellationToken cancellationToken
        ) =>
            server
                .Handle(LspMethods.TextDocumentReferences, parameters)
                .WaitAsync(cancellationToken);

        [JsonRpcMethod(
            LspMethods.TextDocumentDocumentHighlight,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> DocumentHighlightAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TextDocumentDocumentHighlight, parameters);

        [JsonRpcMethod(
            LspMethods.TextDocumentDiagnostic,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> DiagnosticAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TextDocumentDiagnostic, parameters);

        [JsonRpcMethod(
            LspMethods.TextDocumentGetProjectContexts,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> ProjectContextsAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TextDocumentGetProjectContexts, parameters);

        [JsonRpcMethod(
            LspMethods.TextDocumentCodeAction,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> CodeActionAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TextDocumentCodeAction, parameters);

        [JsonRpcMethod(
            LspMethods.TextDocumentPrepareCallHierarchy,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> PrepareCallHierarchyAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TextDocumentPrepareCallHierarchy, parameters);

        [JsonRpcMethod(
            LspMethods.CallHierarchyIncomingCalls,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> IncomingCallsAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.CallHierarchyIncomingCalls, parameters);

        [JsonRpcMethod(
            LspMethods.CallHierarchyOutgoingCalls,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> OutgoingCallsAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.CallHierarchyOutgoingCalls, parameters);

        [JsonRpcMethod(
            LspMethods.TextDocumentPrepareTypeHierarchy,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> PrepareTypeHierarchyAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TextDocumentPrepareTypeHierarchy, parameters);

        [JsonRpcMethod(
            LspMethods.TypeHierarchySupertypes,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> SupertypesAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TypeHierarchySupertypes, parameters);

        [JsonRpcMethod(
            LspMethods.TypeHierarchySubtypes,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> SubtypesAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.TypeHierarchySubtypes, parameters);

        [JsonRpcMethod(
            LspMethods.CodeActionResolveFixAll,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> ResolveFixAllAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.CodeActionResolveFixAll, parameters);

        [JsonRpcMethod(
            LspMethods.WorkspaceExecuteCommand,
            UseSingleObjectParameterDeserialization = true
        )]
        public Task<JsonNode?> WorkspaceExecuteCommandAsync(JsonNode? parameters) =>
            server.Handle(LspMethods.WorkspaceExecuteCommand, parameters);

        [JsonRpcMethod(LspMethods.Shutdown)]
        public static void Shutdown() { }

        [JsonRpcMethod(LspMethods.Exit)]
        public void Exit() => server.Crash();
    }
}
