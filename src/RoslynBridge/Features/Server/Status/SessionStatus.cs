// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Results;

namespace RoslynBridge.Features.Server.Status;

/// <param name="Server">Null when the bridge finds the server itself.</param>
internal sealed record SessionStatus(
    bool Running,
    bool Loading,
    string Workspace,
    string? Server,
    string? Progress = null
) : ToolResult
{
    internal override void Write(StringBuilder text, int budget)
    {
        text.Append("running: ")
            .Append(Running ? "true" : "false")
            .Append("\nloading: ")
            .Append(Loading ? "true" : "false")
            .Append('\n');
        if (Progress is { } progress)
            text.Append("progress: ").Append(progress).Append('\n');

        text.Append("workspace: ").Append(Workspace).Append('\n');
        if (Server is { } server)
            text.Append("server: ").Append(server).Append('\n');
    }
}
