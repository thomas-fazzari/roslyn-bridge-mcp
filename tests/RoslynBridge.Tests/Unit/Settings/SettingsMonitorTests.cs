// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Settings;

namespace RoslynBridge.Tests.Unit.Settings;

public sealed class SettingsMonitorTests
{
    private int _loads;

    [Fact]
    public void ReloadReadsTheOptionsAgain()
    {
        var monitor = new SettingsMonitor(Load);
        var first = monitor.CurrentValue;

        var second = monitor.Reload();

        second.Should().NotBeSameAs(first);
        monitor.CurrentValue.Should().BeSameAs(second);
    }

    private BridgeOptions Load() => new() { StartupTimeout = TimeSpan.FromSeconds(++_loads) };
}
