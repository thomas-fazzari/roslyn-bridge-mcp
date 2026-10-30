// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RoslynBridge.Requests;

namespace RoslynBridge.Tools;

[McpServerToolType]
internal sealed class ServerTool(LspTool lsp)
{
    internal const string Name = "lsp";

    [McpServerTool(
        Name = Name,
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false
    )]
    [Description(
        "Inspect or reload the Roslyn session, or send a raw LSP request. Raw requests use zero-based LSP positions. Unknown methods and workspace/executeCommand require apply=true because they may write files."
    )]
    public Task<CallToolResult> ServerAsync(
        [Description("Session operation, or request for a raw LSP call.")] ServerAction action,
        [Description("LSP method for action=request.")] string? method = null,
        [Description("Raw LSP parameters for action=request.")] JsonObject? parameters = null,
        [Description("Allow a raw request that may change the workspace.")] bool apply = false,
        CancellationToken cancellationToken = default
    ) =>
        lsp.ExecuteAsync(
            new LspRequest
            {
                Action = action switch
                {
                    ServerAction.Status => LspAction.Status,
                    ServerAction.Capabilities => LspAction.Capabilities,
                    ServerAction.Reload => LspAction.Reload,
                    ServerAction.Request => LspAction.Request,
                    _ => throw new ArgumentOutOfRangeException(nameof(action)),
                },
                Method = method,
                Parameters = parameters,
                Apply = apply,
            },
            cancellationToken
        );
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ServerAction
{
    [JsonStringEnumMemberName("status")]
    Status = 0,

    [JsonStringEnumMemberName("capabilities")]
    Capabilities = 1,

    [JsonStringEnumMemberName("reload")]
    Reload = 2,

    [JsonStringEnumMemberName("request")]
    Request = 3,
}
