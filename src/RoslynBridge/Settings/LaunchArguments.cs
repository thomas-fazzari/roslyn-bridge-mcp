// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;

namespace RoslynBridge.Settings;

/// <summary>
/// What the command line sets, where an unset timeout is null so the settings file can fill it.
/// </summary>
internal sealed record LaunchArguments
{
    internal const string WorkspaceArgument = "--workspace";
    internal const string ServerArgument = "--server";
    internal const string DotnetRootArgument = "--dotnet-root";
    internal const string StartupTimeoutArgument = "--startup-timeout";
    internal const string RequestTimeoutArgument = "--request-timeout";
    internal const string ExcludeArgument = "--exclude";

    public string WorkspaceRoot { get; init; } = Directory.GetCurrentDirectory();

    public string? ServerPath { get; init; }

    public string? DotnetRoot { get; init; }

    public TimeSpan? StartupTimeout { get; init; }

    public TimeSpan? RequestTimeout { get; init; }

    public IReadOnlyList<string> Exclude { get; init; } = [];

    public static LaunchArguments Parse(string[] args)
    {
        var arguments = new LaunchArguments();
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for {args[index]}.", nameof(args));
            }

            var value = args[index + 1];
            arguments = args[index] switch
            {
                WorkspaceArgument => arguments with { WorkspaceRoot = Path.GetFullPath(value) },
                ServerArgument => arguments with { ServerPath = value },
                DotnetRootArgument => arguments with { DotnetRoot = Path.GetFullPath(value) },
                StartupTimeoutArgument => arguments with { StartupTimeout = ParseTimeout(value) },
                RequestTimeoutArgument => arguments with { RequestTimeout = ParseTimeout(value) },
                ExcludeArgument => arguments with
                {
                    Exclude = [.. arguments.Exclude, FolderName(value)],
                },
                _ => throw new ArgumentException($"Unknown option: {args[index]}.", nameof(args)),
            };
        }

        return arguments;
    }

    private static string FolderName(string value) =>
        ProjectSettingsValidator.IsFolderName(value)
            ? value
            : throw new ArgumentException($"{ExcludeArgument} takes a folder name.", nameof(value));

    private static TimeSpan ParseTimeout(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
        && ProjectSettingsValidator.IsTimeout(seconds)
            ? TimeSpan.FromSeconds(seconds)
            : throw new ArgumentException(
                $"Timeout must be between {ProjectSettings.MinimumTimeoutSeconds} and {ProjectSettings.MaximumTimeoutSeconds} seconds.",
                nameof(value)
            );
}
