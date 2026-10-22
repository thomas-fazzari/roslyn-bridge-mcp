// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Results;

namespace RoslynBridge;

/// <summary>
/// An expected failure, reported to the client as the structured error of a tool response.
/// </summary>
/// <param name="Candidates">Values the client can send instead, such as the symbols an ambiguous or unknown name may mean.</param>
internal sealed record ToolError(
    string Code,
    string Message,
    IReadOnlyList<SymbolItem>? Candidates = null
)
{
    public static ToolError InvalidRequest(string message) =>
        new(ErrorCodes.InvalidRequest, message);

    public static ToolError InvalidLspData(string message) => new(ErrorCodes.Lsp, message);

    public static ToolError StaleEdit(string message) => new(ErrorCodes.StaleEdit, message);
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

internal static class ResultExtensions
{
    extension<T>(Result<T> result)
        where T : ToolResult
    {
        public Result<ToolResult> AsToolResult() =>
            result.Error is { } error ? error : result.Value;
    }
}

/// <summary>
/// The error codes that tool responses report.
/// </summary>
internal static class ErrorCodes
{
    internal const string InvalidRequest = "invalid_request";
    internal const string StaleEdit = "stale_edit";
    internal const string Loading = "loading";
    internal const string Timeout = "timeout";
    internal const string Cancelled = "cancelled";
    internal const string Lsp = "lsp_error";
    internal const string Io = "io_error";
    internal const string ResultTooLarge = "result_too_large";
    internal const string Internal = "internal_error";
    internal const string SymbolNotFound = "symbol_not_found";
    internal const string AmbiguousSymbol = "ambiguous_symbol";
    internal const string AmbiguousFile = "ambiguous_file";
}
