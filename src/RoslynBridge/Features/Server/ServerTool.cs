// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using RoslynBridge.Common;
using RoslynBridge.Features.Server.Capabilities;
using RoslynBridge.Features.Server.Raw;
using RoslynBridge.Features.Server.Reload;
using RoslynBridge.Features.Server.Status;
using RoslynBridge.Results;

namespace RoslynBridge.Features.Server;

internal sealed class ServerTool(
    ToolPipeline pipeline,
    IToolHandler<StatusRequest, SessionStatus> status,
    IToolHandler<ReloadRequest, SessionStatus> reload,
    IToolHandler<CapabilitiesRequest, JsonResult> capabilities,
    IToolHandler<RawRequest, ToolResult> raw
) : IRoslynTool
{
    public static string Name => "lsp";

    public static string Description =>
        "Inspect or reload the Roslyn session, or send a raw LSP request. Raw requests use zero-based LSP positions. Unknown methods and workspace/executeCommand require apply=true because they may write files.";

    public static bool ReadOnly => false;

    public Task<CallToolResult> ServerAsync(
        [Description("Session operation, or request for a raw LSP call.")] ServerAction action,
        [Description("LSP method for action=request.")] string? method = null,
        [Description("Raw LSP parameters for action=request.")] JsonObject? parameters = null,
        [Description("Allow a raw request that may change the workspace.")] bool apply = false,
        CancellationToken cancellationToken = default
    ) =>
        action switch
        {
            ServerAction.Status => pipeline.RunAsync(
                status,
                new StatusRequest(),
                cancellationToken
            ),
            ServerAction.Capabilities => pipeline.RunAsync(
                capabilities,
                new CapabilitiesRequest(),
                cancellationToken
            ),
            ServerAction.Reload => pipeline.RunAsync(
                reload,
                new ReloadRequest(),
                cancellationToken
            ),
            ServerAction.Request => pipeline.RunAsync(
                raw,
                RawRequest.From(method, parameters, apply),
                cancellationToken
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
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
