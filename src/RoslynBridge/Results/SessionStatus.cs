// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Results;

/// <param name="Server">Null when the bridge finds the server itself.</param>
internal sealed record SessionStatus(
    bool Running,
    bool Loading,
    string Workspace,
    string? Server,
    string? Progress = null
) : ToolResult;
