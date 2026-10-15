// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace RoslynBridge.Results;

/// <param name="Server">The server executable passed as an option, or null when the bridge finds it.</param>
internal sealed record SessionStatus(bool Running, bool Loading, string Workspace, string? Server)
    : ToolResult;
