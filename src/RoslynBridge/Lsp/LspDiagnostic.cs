// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp;

/// <param name="Code">A string or a number.</param>
internal sealed record LspDiagnostic(
    LspRange Range,
    string Message,
    int? Severity = null,
    JsonElement? Code = null
)
{
    public string? CodeText() =>
        Code is { ValueKind: JsonValueKind.String or JsonValueKind.Number } code
            ? code.ToString()
            : null;
}

/// <param name="Kind">Full when the report lists the diagnostics, or unchanged when the result id still holds.</param>
internal sealed record LspDiagnosticReport(
    string Kind,
    string? ResultId = null,
    LspDiagnostic[]? Items = null
);
