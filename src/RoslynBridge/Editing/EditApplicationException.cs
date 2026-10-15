// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using RoslynBridge.Results;

namespace RoslynBridge.Editing;

internal sealed class EditApplicationException(
    EditReceipt result,
    EditApplicationPhase phase,
    string? failedPath,
    string? failedOperation,
    Exception innerException
) : Exception("The workspace change did not fully complete.", innerException)
{
    public EditReceipt Result { get; } = result;

    public EditApplicationPhase Phase { get; } = phase;

    public string? FailedPath { get; } = failedPath;

    public string? FailedOperation { get; } = failedOperation;
}
