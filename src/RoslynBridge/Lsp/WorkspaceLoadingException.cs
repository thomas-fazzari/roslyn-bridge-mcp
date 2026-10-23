// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp;

/// <summary>
/// Reports that Roslyn is still loading the workspace when a caller stops waiting. The load continues.
/// </summary>
/// <param name="progress">What Roslyn reported so far, or null when it reported nothing.</param>
internal sealed class WorkspaceLoadingException(LoadProgress? progress)
    : Exception(Describe(progress))
{
    private const string Loading = "Roslyn is still loading the workspace.";
    private const string Advice = "Retry the call, or use lsp status to follow the load.";

    private static string Describe(LoadProgress? progress) =>
        progress is null ? $"{Loading} {Advice}" : $"{Loading} Progress: {progress.Text}. {Advice}";
}
