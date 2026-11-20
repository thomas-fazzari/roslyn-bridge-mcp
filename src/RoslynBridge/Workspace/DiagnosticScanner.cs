// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Requests;
using RoslynBridge.Results;

namespace RoslynBridge.Workspace;

/// <param name="Ids">Only these diagnostic codes. Null or empty keeps every code.</param>
internal sealed record DiagnosticFilter(
    DiagnosticSeverity Severity,
    IReadOnlyList<string>? Ids,
    bool Summary,
    Paging Page
)
{
    public static DiagnosticFilter Default { get; } =
        new(DiagnosticSeverity.Warning, Ids: null, Summary: false, Paging.Default);
}

internal sealed class DiagnosticScanner(RoslynSession session, WorkspacePaths paths)
{
    /// <param name="files">Full paths of C# files.</param>
    public async Task<Result<DiagnosticReport>> ScanAsync(
        IReadOnlyCollection<string> files,
        DiagnosticFilter filter,
        CancellationToken cancellationToken
    )
    {
        if (await session.SynchronizeNewFilesAsync(files, cancellationToken) is ToolError error)
            return error;

        var scanned = new List<ScannedFile>(files.Count);
        foreach (var batch in files.Chunk(RoslynConnection.MaximumOpenDocuments))
        {
            var batchScan = await ScanBatchAsync(batch, cancellationToken);
            if (batchScan.IsError(out var scanError, out var batchFiles))
                return scanError;

            scanned.AddRange(batchFiles);
        }

        return Report(scanned, filter);
    }

    private async Task<Result<List<ScannedFile>>> ScanBatchAsync(
        string[] batch,
        CancellationToken cancellationToken
    )
    {
        // Each open discards diagnostics of earlier solution versions, so the batch opens before any pull
        var opened = await session.OpenDocumentsAsync(batch, cancellationToken);
        if (opened.IsError(out var openError, out var uris))
            return openError;

        var projects = await session.WaitForProjectsAsync(batch, cancellationToken);
        if (projects.IsError(out var waitError, out var miscellaneous))
            return waitError;

        var reports = await Task.WhenAll(
            uris.Select(uri => session.DiagnosticReportAsync(uri, cancellationToken))
        );
        return reports
            .All()
            .Map(read =>
                batch
                    .Zip(read, (file, report) =>
                        new ScannedFile(file, report.Items ?? [], miscellaneous.Contains(file))
                    )
                    .ToList()
            );
    }

    private DiagnosticReport Report(List<ScannedFile> scanned, DiagnosticFilter filter)
    {
        var page = filter.Page;
        var ids = filter.Ids is { Count: > 0 }
            ? filter.Ids.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var found = new List<(string File, Diagnostic Diagnostic)>();
        var belowSeverity = 0;
        foreach (var file in scanned)
        {
            var matching = file
                .Reported.Where(diagnostic =>
                    ids is null || (diagnostic.CodeText() is { } code && ids.Contains(code))
                )
                .ToList();
            var selected = matching
                .Where(diagnostic => diagnostic.ToSeverity() <= filter.Severity)
                .ToList();
            belowSeverity += matching.Count - selected.Count;
            if (selected.Count == 0)
                continue;

            var relative = paths.RelativePath(file.Path);
            found.AddRange(
                selected
                    .Select(diagnostic => diagnostic.ToDiagnostic())
                    .OrderBy(diagnostic => diagnostic.Start.Line)
                    .ThenBy(diagnostic => diagnostic.Start.Character)
                    .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                    .Select(diagnostic => (relative, diagnostic))
            );
        }

        var codes = filter.Summary ? Summarize(found) : [];
        var listed = filter.Summary ? [] : page.Slice(found);
        return new DiagnosticReport(
            scanned.Count,
            [
                .. listed
                    .GroupBy(entry => entry.File, StringComparer.Ordinal)
                    .Select(group => new FileDiagnostics(
                        group.Key,
                        [.. group.Select(entry => entry.Diagnostic)]
                    )),
            ],
            [.. page.Slice(codes)],
            found.Select(entry => entry.File).Distinct(StringComparer.Ordinal).Count(),
            found.Count,
            belowSeverity,
            page.NextOffset(filter.Summary ? codes.Count : found.Count),
            [
                .. scanned
                    .Where(file => file.Miscellaneous)
                    .Select(file => paths.RelativePath(file.Path)),
            ],
            codes.Count,
            ids is not null
        );
    }

    private static List<CodeSummary> Summarize(List<(string File, Diagnostic Diagnostic)> found) =>
        [
            .. found
                .GroupBy(entry => entry.Diagnostic.Code, StringComparer.Ordinal)
                .Select(group => new CodeSummary(
                    group.Count(),
                    group.First().File,
                    group.First().Diagnostic
                ))
                .OrderByDescending(summary => summary.Count)
                .ThenBy(summary => summary.Example.Code, StringComparer.Ordinal),
        ];

    private sealed record ScannedFile(string Path, LspDiagnostic[] Reported, bool Miscellaneous);
}

internal static class LspDiagnosticExtensions
{
    extension(LspDiagnostic diagnostic)
    {
        // LSP leaves a missing severity to the client. Reporting it as an error keeps it visible
        public DiagnosticSeverity ToSeverity() =>
            diagnostic.Severity is { } severity && Enum.IsDefined((DiagnosticSeverity)severity)
                ? (DiagnosticSeverity)severity
                : DiagnosticSeverity.Error;

        public Diagnostic ToDiagnostic() =>
            new(
                diagnostic.Range.Start.OneBased,
                diagnostic.ToSeverity(),
                diagnostic.CodeText(),
                diagnostic.Message
            );
    }
}
