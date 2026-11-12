// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;

namespace RoslynBridge.Settings;

internal sealed record BridgeOptions
{
    internal const int DefaultStartupTimeoutSeconds = 120;
    internal const int DefaultRequestTimeoutSeconds = 60;

    public string WorkspaceRoot { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>
    /// Null lets the bridge find the server.
    /// </summary>
    public string? ServerPath { get; init; }

    /// <summary>
    /// Null lets the bridge find the install.
    /// </summary>
    public string? DotnetRoot { get; init; }

    public TimeSpan StartupTimeout { get; init; } =
        TimeSpan.FromSeconds(DefaultStartupTimeoutSeconds);

    public TimeSpan RequestTimeout { get; init; } =
        TimeSpan.FromSeconds(DefaultRequestTimeoutSeconds);

    /// <summary>
    /// Folder names left out in addition to the built-in list.
    /// </summary>
    public FrozenSet<string> Exclude { get; init; } = [];

    /// <summary>
    /// A full path. Null lets Roslyn find what to load.
    /// </summary>
    public string? Solution { get; init; }

    /// <summary>
    /// Full paths. Empty lets Roslyn find what to load.
    /// </summary>
    public IReadOnlyList<string> Projects { get; init; } = [];

    /// <summary>
    /// Null uses the built-in default.
    /// </summary>
    public DiagnosticSeverity? DiagnosticsSeverity { get; init; }

    /// <summary>
    /// Arguments win over the settings file. The excluded folders of both apply.
    /// </summary>
    public static BridgeOptions From(LaunchArguments arguments, ProjectSettings settings) =>
        new()
        {
            WorkspaceRoot = arguments.WorkspaceRoot,
            ServerPath = arguments.ServerPath,
            DotnetRoot = arguments.DotnetRoot,
            StartupTimeout = arguments.StartupTimeout
                ?? Seconds(settings.StartupTimeout)
                ?? TimeSpan.FromSeconds(DefaultStartupTimeoutSeconds),
            RequestTimeout = arguments.RequestTimeout
                ?? Seconds(settings.RequestTimeout)
                ?? TimeSpan.FromSeconds(DefaultRequestTimeoutSeconds),
            Exclude = (settings.Exclude ?? [])
                .Concat(arguments.Exclude)
                .ToFrozenSet(StringComparer.Ordinal),
            Solution = FullPath(arguments, settings.Solution),
            Projects =
            [
                .. (settings.Projects ?? []).Select(project =>
                    Path.GetFullPath(project, arguments.WorkspaceRoot)
                ),
            ],
            DiagnosticsSeverity = settings.Diagnostics?.Severity,
        };

    private static TimeSpan? Seconds(int? seconds) =>
        seconds is { } value ? TimeSpan.FromSeconds(value) : null;

    private static string? FullPath(LaunchArguments arguments, string? path) =>
        path is null ? null : Path.GetFullPath(path, arguments.WorkspaceRoot);
}
