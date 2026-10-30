// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Concurrent;

namespace RoslynBridge.Lsp;

internal sealed partial class RoslynSession
{
    private const string UnchangedReport = "unchanged";

    private readonly ConcurrentDictionary<string, LspDiagnosticReport> _diagnosticReports = new(
        StringComparer.Ordinal
    );

    /// <summary>
    /// Pulls the diagnostics of a document, sending the result id of its last report so that
    /// Roslyn can answer that nothing changed instead of sending the same report again.
    /// </summary>
    /// <returns>The full diagnostic report of the document.</returns>
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
            return previous;
        }

        _diagnosticReports[uri] = report;
        return report;
    }

    private void ClearDiagnosticReports() => _diagnosticReports.Clear();
}
