// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Protocol;
using RoslynBridge.Common;
using RoslynBridge.Requests;
using RoslynBridge.Results;

namespace RoslynBridge.Features.Diagnostics;

internal sealed class DiagnosticsTool(
    ToolPipeline pipeline,
    IToolHandler<DiagnosticsRequest, DiagnosticReport> handler
) : IRoslynTool
{
    public static string Name => "diagnostics";

    public static string Description =>
        "Report C# compiler and analyzer diagnostics for saved files, with one-based positions.";

    public static bool ReadOnly => true;

    public Task<CallToolResult> DiagnosticsAsync(
        [Description(
            "Workspace-relative file path or glob. Braces list several, as in src/{A,B}.cs. Omitting it scans every C# file in the workspace."
        )]
            string? file = null,
        [Description(
            "Lowest severity to report. The default is the project's diagnostics.severity setting, or else warning, which reports errors and warnings. Use information or hint to include analyzer suggestions."
        )]
            DiagnosticSeverity? severity = null,
        [Description("Only these diagnostic codes, such as CS0246.")] string[]? ids = null,
        [Description("One line per code with count and example.")] bool summary = false,
        [Description(Paging.LimitDescription)]
        [Range(1, Paging.MaximumLimit)]
            int limit = Paging.DefaultLimit,
        [Description(Paging.OffsetDescription)] [Range(0, int.MaxValue)] int offset = 0,
        CancellationToken cancellationToken = default
    ) =>
        pipeline.RunAsync(
            handler,
            new DiagnosticsRequest(file, severity, ids, summary, new Paging(limit, offset)),
            cancellationToken
        );
}
