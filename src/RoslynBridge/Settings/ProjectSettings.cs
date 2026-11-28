// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Settings;

internal sealed record ProjectSettings
{
    internal const string FILE_NAME = ".roslyn-bridge.json";
    internal const int MINIMUM_TIMEOUT_SECONDS = 1;
    internal const int MAXIMUM_TIMEOUT_SECONDS = 600;

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
