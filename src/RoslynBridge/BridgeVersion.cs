// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge;

internal static class BridgeVersion
{
    // The SDK appends "+commit" to the informational version
    public static readonly string Current = typeof(BridgeVersion)
        .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+')[0];
}
