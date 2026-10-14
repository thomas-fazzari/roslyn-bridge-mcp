// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace RoslynBridge.Lsp;

/// <summary>
/// Reports that Roslyn is still loading the workspace when a caller stops waiting. The load continues.
/// </summary>
internal sealed class WorkspaceLoadingException()
    : Exception(
        "Roslyn is still loading the workspace. Retry the call, or use lsp status to follow the load."
    );
