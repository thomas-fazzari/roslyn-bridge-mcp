// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Settings;

/// <summary>
/// The command line holds an unknown option or a value that cannot apply.
/// </summary>
internal sealed class InvalidLaunchArgumentsException(string message) : Exception(message);
