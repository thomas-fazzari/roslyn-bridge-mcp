// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Settings;

/// <summary>
/// Checks settings against the workspace. The command line shares the folder and timeout rules.
/// </summary>
internal static class ProjectSettingsValidator
{
    private static readonly string[] _solutionExtensions = [".sln", ".slnx", ".slnf"];
    private static readonly string[] _projectExtensions = [".csproj"];

    /// <returns>Every fault of the settings, which is empty when they are valid.</returns>
    public static List<string> Faults(string root, ProjectSettings settings)
    {
        var faults = new List<string>();
        foreach (var folder in settings.Exclude ?? [])
        {
            if (!IsFolderName(folder))
            {
                faults.Add($"exclude entry '{folder}' is not a folder name");
            }
        }

        CheckTimeout("startupTimeout", settings.StartupTimeout, faults);
        CheckTimeout("requestTimeout", settings.RequestTimeout, faults);

        if (settings.Solution is not null)
        {
            CheckPath(root, "solution", settings.Solution, _solutionExtensions, faults);
        }

        foreach (var project in settings.Projects ?? [])
        {
            CheckPath(root, "projects", project, _projectExtensions, faults);
        }

        if (settings.Solution is not null && settings.Projects is { Length: > 0 })
        {
            faults.Add("solution and projects cannot both be set");
        }

        if (settings.Diagnostics?.Severity is { } severity && !Enum.IsDefined(severity))
        {
            faults.Add("diagnostics.severity must be error, warning, information or hint");
        }

        return faults;
    }

    /// <returns>Whether the value names one folder, without a path separator.</returns>
    public static bool IsFolderName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.AsSpan().ContainsAny('/', '\\');

    /// <returns>Whether the number of seconds is an allowed timeout.</returns>
    public static bool IsTimeout(int seconds) =>
        seconds
            is >= ProjectSettings.MinimumTimeoutSeconds
                and <= ProjectSettings.MaximumTimeoutSeconds;

    private static void CheckTimeout(string key, int? seconds, List<string> faults)
    {
        if (seconds is { } value && !IsTimeout(value))
        {
            faults.Add(
                $"{key} must be from {ProjectSettings.MinimumTimeoutSeconds} to {ProjectSettings.MaximumTimeoutSeconds} seconds"
            );
        }
    }

    private static void CheckPath(
        string root,
        string key,
        string text,
        string[] extensions,
        List<string> faults
    )
    {
        var full = Path.GetFullPath(text, root);
        if (WorkspacePaths.IsOutside(Path.GetRelativePath(root, full)))
        {
            faults.Add($"{key} '{text}' is outside the workspace");
        }
        else if (!extensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
        {
            faults.Add($"{key} '{text}' must end with {string.Join(", ", extensions)}");
        }
        else if (!File.Exists(full))
        {
            faults.Add($"{key} '{text}' does not exist");
        }
    }
}
