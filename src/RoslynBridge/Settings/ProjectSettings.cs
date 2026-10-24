// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;

namespace RoslynBridge.Settings;

/// <summary>
/// The settings of <c>.roslyn-bridge.json</c>, as the file spells them.
/// </summary>
internal sealed record ProjectSettings
{
    internal const string FileName = ".roslyn-bridge.json";
    internal const int MinimumTimeoutSeconds = 1;
    internal const int MaximumTimeoutSeconds = 600;

    public string[]? Exclude { get; init; }

    public int? StartupTimeout { get; init; }

    public int? RequestTimeout { get; init; }

    public string? Solution { get; init; }

    public string[]? Projects { get; init; }

    public DiagnosticsSettings? Diagnostics { get; init; }
}

internal sealed record DiagnosticsSettings
{
    public DiagnosticSeverity? Severity { get; init; }
}
