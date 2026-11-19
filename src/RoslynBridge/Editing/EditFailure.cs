// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Results;

namespace RoslynBridge.Editing;

/// <summary>
/// A workspace change that stopped after it wrote files.
/// The receipt lists what it wrote.
/// </summary>
internal sealed record EditFailure : ToolError
{
    public EditFailure(
        ToolError cause,
        EditReceipt receipt,
        EditApplicationPhase phase,
        string? failedPath = null,
        string? failedOperation = null
    )
        : base(cause.Code, cause.Message)
    {
        Receipt = receipt;
        Phase = phase;
        FailedPath = failedPath;
        FailedOperation = failedOperation;
    }

    public EditReceipt Receipt { get; init; }

    public EditApplicationPhase Phase { get; }

    public string? FailedPath { get; }

    public string? FailedOperation { get; }
}
