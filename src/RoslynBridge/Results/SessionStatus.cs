// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Results;

/// <param name="Server">The server executable passed as an option, or null when the bridge finds it.</param>
/// <param name="Progress">What Roslyn reports about the load in progress, or null when it is not loading or reported none.</param>
internal sealed record SessionStatus(
    bool Running,
    bool Loading,
    string Workspace,
    string? Server,
    string? Progress = null
) : ToolResult;
