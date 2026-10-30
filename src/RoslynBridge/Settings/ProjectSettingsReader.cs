// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Settings;

internal static class ProjectSettingsReader
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Reads the settings file of the workspace root.
    /// </summary>
    /// <returns>The settings, which are empty when there is no file.</returns>
    /// <exception cref="InvalidSettingsException">The file cannot be read, has an unknown key, or holds a faulty setting.</exception>
    public static ProjectSettings Read(string root)
    {
        var path = Path.Combine(root, ProjectSettings.FileName);
        if (!File.Exists(path))
        {
            return new ProjectSettings();
        }

        ProjectSettings settings;
        try
        {
            using var file = File.OpenRead(path);
            settings =
                JsonSerializer.Deserialize<ProjectSettings>(file, _options)
                ?? new ProjectSettings();
        }
        catch (Exception exception)
            when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidSettingsException([exception.Message]);
        }

        var faults = ProjectSettingsValidator.Faults(root, settings);
        return faults.Count == 0 ? settings : throw new InvalidSettingsException(faults);
    }
}
