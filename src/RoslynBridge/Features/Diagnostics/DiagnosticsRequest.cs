// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;

namespace RoslynBridge.Features.Diagnostics;

internal sealed record DiagnosticsRequest(
    string? File,
    DiagnosticSeverity? Severity,
    IReadOnlyList<string>? Ids,
    bool Summary,
    Paging Page
) : ToolRequest
{
    public override Paging Page { get; } = Page;

    public override CallPolicy Policy =>
        new(LspAction.Diagnostics, ServerPreparation.Start, Files: FileScope.CSharpOrGlob);

    public override DiagnosticsRequest WithFiles(FileResolver resolve) =>
        this with
        {
            File = resolve(File),
        };
}
