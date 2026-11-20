// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Common;
using RoslynBridge.Results;
using RoslynBridge.Settings;
using RoslynBridge.Workspace;

namespace RoslynBridge.Features.Diagnostics;

internal sealed class DiagnosticsHandler(
    FileSelection selection,
    DiagnosticScanner scanner,
    SettingsMonitor settings
) : IToolHandler<DiagnosticsRequest, DiagnosticReport>
{
    public async Task<Result<DiagnosticReport>> HandleAsync(
        DiagnosticsRequest request,
        CancellationToken cancellationToken
    )
    {
        var selected = selection.SelectAtMost(request.File, "Diagnostics", cancellationToken);
        if (selected.IsError(out var error, out var files))
            return error;

        var severity = request.Severity
            ?? settings.CurrentValue.DiagnosticsSeverity
            ?? DiagnosticSeverity.Warning;
        return await scanner.ScanAsync(
            files,
            new DiagnosticFilter(severity, request.Ids, request.Summary, request.Page),
            cancellationToken
        );
    }
}
