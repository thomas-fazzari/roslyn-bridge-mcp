// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;

namespace RoslynBridge.Tools;

/// <summary>
/// An expected failure, reported to the client as the structured error of a tool response.
/// </summary>
/// <param name="Candidates">Values the client can send instead, such as the symbols an ambiguous name matches.</param>
internal sealed record ToolError(string Code, string Message, JsonArray? Candidates = null)
{
    public static ToolError InvalidRequest(string message) =>
        new(LspTool.InvalidRequestErrorCode, message);

    public static ToolError InvalidLspData(string message) => new(LspTool.LspErrorCode, message);

    public static ToolError StaleEdit(string message) => new(LspTool.StaleEditErrorCode, message);
}

/// <summary>
/// Reports a <see cref="ToolError"/> from code too deep in a call chain to return a <see cref="Result{T}"/>.
/// </summary>
internal sealed class ToolErrorException(ToolError error) : Exception(error.Message)
{
    public ToolError Error { get; } = error;
}

/// <summary>
/// The value of an operation, or the <see cref="ToolError"/> that prevented it.
/// </summary>
internal readonly struct Result<T>(T value, ToolError? error)
{
    /// <summary>The value, which is meaningful only when <see cref="Error"/> is null.</summary>
    public T Value { get; } = value;

    public ToolError? Error { get; } = error;

    public static implicit operator Result<T>(T value) => new(value, error: null);

    public static implicit operator Result<T>(ToolError error) => new(default!, error);
}
