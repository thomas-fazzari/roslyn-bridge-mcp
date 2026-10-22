// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RoslynBridge.Requests;

namespace RoslynBridge.Tools;

[McpServerToolType]
internal sealed class DiagnosticsTool(LspTool lsp)
{
    internal const string Name = "diagnostics";

    [McpServerTool(
        Name = Name,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "Report C# compiler and analyzer diagnostics for saved files, with one-based positions."
    )]
    public Task<CallToolResult> DiagnosticsAsync(
        [Description(
            "Workspace-relative file path or glob. Braces list several, as in src/{A,B}.cs. Omitting it scans every C# file in the workspace."
        )]
            string? file = null,
        [Description(
            "Lowest severity to report. The default reports errors and warnings. Use information or hint to include analyzer suggestions."
        )]
            DiagnosticSeverity severity = DiagnosticSeverity.Warning,
        [Description(ToolDescriptions.Limit)] int limit = LspRequest.DefaultResultLimit,
        CancellationToken cancellationToken = default
    ) =>
        lsp.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Diagnostics,
                File = file,
                Severity = severity,
                Limit = limit,
            },
            cancellationToken
        );
}
