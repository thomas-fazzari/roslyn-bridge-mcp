// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Settings;

/// <summary>
/// The settings file is unreadable or holds settings that cannot apply.
/// </summary>
internal sealed class InvalidSettingsException(IReadOnlyList<string> faults)
    : Exception($"{ProjectSettings.FileName} is invalid:\n- {string.Join("\n- ", faults)}")
{
    public IReadOnlyList<string> Faults { get; } = faults;
}
