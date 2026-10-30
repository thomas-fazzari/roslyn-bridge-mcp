// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace RoslynBridge.Lsp;

/// <summary>
/// Answers Roslyn requests and consumes its notifications while tool calls are pending.
/// </summary>
internal sealed partial class ClientCallbacks(
    RoslynSession session,
    WorkspacePaths paths,
    ILogger<RoslynSession> logger,
    TaskCompletionSource initialized
)
{
    private const int MaximumLogMessageCharacters = 2048;

    [JsonRpcMethod(
        LspMethods.WorkspaceConfiguration,
        UseSingleObjectParameterDeserialization = true
    )]
    public static object?[] Configuration(ConfigurationParams parameters) =>
        new object?[parameters.Items.Length];

    [JsonRpcMethod(LspMethods.WorkspaceWorkspaceFolders)]
    public JsonArray WorkspaceFolders() => ClientCapabilities.WorkspaceFolders(paths);

    [JsonRpcMethod(LspMethods.WorkspaceApplyEdit, UseSingleObjectParameterDeserialization = true)]
    public Task<ApplyWorkspaceEditResult> ApplyEditAsync(
        ApplyWorkspaceEditParams parameters,
        CancellationToken cancellationToken
    )
    {
        if (session.ApplyWorkspaceEditAsync is { } apply)
        {
            return apply(parameters.Edit, cancellationToken);
        }

        return Task.FromResult(
            new ApplyWorkspaceEditResult(
                Applied: false,
                "No workspace edit is being applied by the current tool call."
            )
        );
    }

    [JsonRpcMethod(
        LspMethods.ClientRegisterCapability,
        UseSingleObjectParameterDeserialization = true
    )]
    public void RegisterCapability(RegistrationParams parameters) =>
        session.RegisterCapabilities(parameters);

    [JsonRpcMethod(
        LspMethods.ClientUnregisterCapability,
        UseSingleObjectParameterDeserialization = true
    )]
    public void UnregisterCapability(UnregistrationParams parameters) =>
        session.UnregisterCapabilities(parameters);

    [JsonRpcMethod(LspMethods.WorkspaceProjectInitializationComplete)]
    public void InitializationComplete() => initialized.TrySetResult();

    // Roslyn waits for this answer before it reports progress
    [JsonRpcMethod(
        LspMethods.WindowWorkDoneProgressCreate,
        UseSingleObjectParameterDeserialization = true
    )]
    public static void CreateProgress(JsonObject _) { }

    [JsonRpcMethod(LspMethods.Progress, UseSingleObjectParameterDeserialization = true)]
    public void ReportProgress(ProgressParams parameters) =>
        session.ReportProgress(parameters.Token.ToString(), parameters.Value);

    [JsonRpcMethod(LspMethods.WindowLogMessage, UseSingleObjectParameterDeserialization = true)]
    public void LogMessage(MessageParams parameters)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var message = parameters.Message;
        var boundedMessage = message
            .AsSpan(0, Math.Min(message.Length, MaximumLogMessageCharacters))
            .ToString();
        LogRoslynMessage(logger, boundedMessage);
    }

    [JsonRpcMethod(LspMethods.WindowShowMessage, UseSingleObjectParameterDeserialization = true)]
    public void ShowMessage(MessageParams parameters) => LogMessage(parameters);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Roslyn: {Message}")]
    private static partial void LogRoslynMessage(ILogger logger, string message);
}
