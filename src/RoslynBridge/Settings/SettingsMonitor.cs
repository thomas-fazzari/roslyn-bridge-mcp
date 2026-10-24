// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Settings;

/// <summary>
/// Holds the effective options, which change only on <see cref="Reload"/>.
/// </summary>
internal sealed class SettingsMonitor(Func<BridgeOptions> load)
{
    private BridgeOptions _current = load();

    public BridgeOptions CurrentValue => Volatile.Read(ref _current);

    /// <summary>
    /// Reads the arguments and the settings file again.
    /// </summary>
    /// <returns>The new options.</returns>
    public BridgeOptions Reload()
    {
        var options = load();
        Volatile.Write(ref _current, options);
        return options;
    }
}
