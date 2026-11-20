// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Results;

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
        FileChange? failedOperation = null
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

    public FileChange? FailedOperation { get; }
}
