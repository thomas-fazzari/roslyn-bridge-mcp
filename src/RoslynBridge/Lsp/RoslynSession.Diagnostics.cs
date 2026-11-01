// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Telemetry;

namespace RoslynBridge.Lsp;

internal sealed partial class RoslynSession
{
    private const string UnchangedReport = "unchanged";

    private readonly ConcurrentDictionary<string, LspDiagnosticReport> _diagnosticReports = new(
        StringComparer.Ordinal
    );

    /// <summary>
    /// Returns the full report, reusing the previous one when Roslyn answers unchanged.
    /// </summary>
    public async Task<LspDiagnosticReport> DiagnosticReportAsync(
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

        var result = await RequestAsync(
            LspMethods.TextDocumentDiagnostic,
            parameters,
            cancellationToken
        );
        var report = result.Read<LspDiagnosticReport>();
        if (previous is not null && report.Kind is UnchangedReport)
        {
            BridgeTelemetry.CountDiagnosticReport(reused: true);
            return previous;
        }

        BridgeTelemetry.CountDiagnosticReport(reused: false);
        _diagnosticReports[uri] = report;
        return report;
    }

    private void ClearDiagnosticReports() => _diagnosticReports.Clear();
}
