// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Settings;

/// <summary>
/// An unset timeout is null, so the settings file can fill it.
/// </summary>
internal sealed record LaunchArguments
{
    internal const string WORKSPACE_ARGUMENT = "--workspace";
    internal const string SERVER_ARGUMENT = "--server";
    internal const string DOTNET_ROOT_ARGUMENT = "--dotnet-root";
    internal const string STARTUP_TIMEOUT_ARGUMENT = "--startup-timeout";
    internal const string REQUEST_TIMEOUT_ARGUMENT = "--request-timeout";
    internal const string EXCLUDE_ARGUMENT = "--exclude";

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
                throw new InvalidLaunchArgumentsException($"Missing value for {args[index]}.");

            var value = args[index + 1];
            arguments = args[index] switch
            {
                WORKSPACE_ARGUMENT => arguments with { WorkspaceRoot = Path.GetFullPath(value) },
                SERVER_ARGUMENT => arguments with { ServerPath = value },
                DOTNET_ROOT_ARGUMENT => arguments with { DotnetRoot = Path.GetFullPath(value) },
                STARTUP_TIMEOUT_ARGUMENT => arguments with { StartupTimeout = ParseTimeout(value) },
                REQUEST_TIMEOUT_ARGUMENT => arguments with { RequestTimeout = ParseTimeout(value) },
                EXCLUDE_ARGUMENT => arguments with
                {
                    Exclude = [.. arguments.Exclude, FolderName(value)],
                },
                _ => throw new InvalidLaunchArgumentsException($"Unknown option: {args[index]}."),
            };
        }

        return arguments;
    }

    private static string FolderName(string value) =>
        ProjectSettingsValidator.IsFolderName(value)
            ? value
            : throw new InvalidLaunchArgumentsException($"{EXCLUDE_ARGUMENT} takes a folder name.");

    private static TimeSpan ParseTimeout(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
        && ProjectSettingsValidator.IsTimeout(seconds)
            ? TimeSpan.FromSeconds(seconds)
            : throw new InvalidLaunchArgumentsException(
                $"Timeout must be between {ProjectSettings.MINIMUM_TIMEOUT_SECONDS} and {ProjectSettings.MAXIMUM_TIMEOUT_SECONDS} seconds."
            );
}
