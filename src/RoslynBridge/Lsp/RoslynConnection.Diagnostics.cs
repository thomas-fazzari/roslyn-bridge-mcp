// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Telemetry;

namespace RoslynBridge.Lsp;

internal sealed partial class RoslynConnection
{
    private const string UNCHANGED_REPORT = "unchanged";

    private readonly ConcurrentDictionary<string, LspDiagnosticReport> _diagnosticReports = new(
        StringComparer.Ordinal
    );

    /// <summary>
    /// Returns the full report, reusing the previous one when Roslyn answers unchanged.
    /// </summary>
    public async Task<Result<LspDiagnosticReport>> DiagnosticReportAsync(
        string uri,
        CancellationToken cancellationToken
    )
    {
        var parameters = LspParameters.Document(uri);
        if (
            _diagnosticReports.TryGetValue(uri, out var previous)
            && previous.ResultId is { } resultId
        )
        {
            parameters["previousResultId"] = resultId;
        }

        var response = await RequestAsync(
            LspMethods.TEXT_DOCUMENT_DIAGNOSTIC,
            parameters,
            cancellationToken
        );
        if (response.IsError(out var error, out var result))
            return error;

        if (result.Read<LspDiagnosticReport>().IsError(out var invalid, out var report))
            return invalid;

        if (previous is not null && report.Kind is UNCHANGED_REPORT)
        {
            BridgeTelemetry.CountDiagnosticReport(reused: true);
            return previous;
        }

        BridgeTelemetry.CountDiagnosticReport(reused: false);
        _diagnosticReports[uri] = report;
        return report;
    }
}
