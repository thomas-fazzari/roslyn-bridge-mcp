// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Results;

namespace RoslynBridge;

/// <param name="Candidates">Values the client can send instead, such as the symbols an ambiguous name may mean.</param>
/// <param name="RpcCode">The JSON-RPC error code of a failed Roslyn request.</param>
internal record ToolError(
    string Code,
    string Message,
    IReadOnlyList<SymbolItem>? Candidates = null,
    int? RpcCode = null
)
{
    public static ToolError InvalidRequest(string message) =>
        new(ErrorCodes.InvalidRequest, message);

    public static ToolError InvalidLspData(string message) => new(ErrorCodes.Lsp, message);

    public static ToolError StaleEdit(string message) => new(ErrorCodes.StaleEdit, message);

    public static ToolError Io(string message) => new(ErrorCodes.Io, message);

    /// <summary>
    /// A cancellation that reaches a step which must report the files it already wrote.
    /// </summary>
    public static ToolError Cancelled(OperationCanceledException exception) =>
        new(ErrorCodes.Cancelled, exception.Message);

    /// <summary>
    /// Whether Roslyn, the file system or a timeout failed, as opposed to a request that is wrong.
    /// </summary>
    public bool IsInfrastructureFailure =>
        Code is ErrorCodes.Lsp or ErrorCodes.Io or ErrorCodes.Timeout;

    /// <summary>
    /// Whether a file system call failed for a reason outside the bridge, such as a locked or unreadable file.
    /// </summary>
    public static bool IsIo(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;
}

internal sealed record Success
{
    private Success() { }

    public static Success Instance { get; } = new();
}

/// <summary>
/// The outcome of an operation that returns no value.
/// </summary>
internal union Result(Success, ToolError)
{
    public static Result Success => RoslynBridge.Success.Instance;
}

/// <summary>
/// The outcome of an operation that returns a value.
/// </summary>
internal union Result<T>(T, ToolError)
{
    /// <summary>
    /// Splits the result so that the value is readable only after the error check.
    /// </summary>
    public bool IsError([NotNullWhen(true)] out ToolError? error, [MaybeNullWhen(true)] out T value)
    {
        if (this is ToolError toolError)
        {
            (error, value) = (toolError, default);
            return true;
        }

        (error, value) = (null, Value is T held ? held : default!);
        return false;
    }

    public Result<TResult> Map<TResult>(Func<T, TResult> map) =>
        this switch
        {
            ToolError error => error,
            T value => map(value),
            null => map(default!),
        };
}

internal static class ResultExtensions
{
    extension<T>(IEnumerable<Result<T>> results)
    {
        /// <summary>
        /// Stops at the first error.
        /// </summary>
        public Result<List<T>> All()
        {
            var values = new List<T>();
            foreach (var result in results)
            {
                if (result.IsError(out var error, out var value))
                {
                    return error;
                }

                values.Add(value);
            }

            return values;
        }
    }

    extension<T>(Result<T> result)
        where T : ToolResult
    {
        public Result<ToolResult> AsToolResult() => result.Map<ToolResult>(value => value);
    }

    extension<T>(Task<Result<T>> task)
    {
        /// <summary>
        /// Turns a cancellation into <see cref="ToolError.Cancelled"/>.
        /// </summary>
        public async Task<Result<T>> OrCancelledAsync()
        {
            try
            {
                return await task;
            }
            catch (OperationCanceledException exception)
            {
                return ToolError.Cancelled(exception);
            }
        }
    }

    extension(Task<Result> task)
    {
        /// <summary>
        /// Turns a cancellation into <see cref="ToolError.Cancelled"/>.
        /// </summary>
        public async Task<Result> OrCancelledAsync()
        {
            try
            {
                return await task;
            }
            catch (OperationCanceledException exception)
            {
                return ToolError.Cancelled(exception);
            }
        }
    }
}

internal static class ErrorCodes
{
    internal const string InvalidRequest = "invalid_request";
    internal const string InvalidSettings = "invalid_settings";
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
