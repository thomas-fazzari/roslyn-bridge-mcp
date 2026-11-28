// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

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
        new(ErrorCodes.INVALID_REQUEST, message);

    public static ToolError InvalidLspData(string message) => new(ErrorCodes.LSP, message);

    public static ToolError StaleEdit(string message) => new(ErrorCodes.STALE_EDIT, message);

    public static ToolError Io(string message) => new(ErrorCodes.IO, message);

    /// <summary>
    /// A cancellation that reaches a step which must report the files it already wrote.
    /// </summary>
    public static ToolError Cancelled(OperationCanceledException exception) =>
        new(ErrorCodes.CANCELLED, exception.Message);

    /// <summary>
    /// Whether Roslyn, the file system or a timeout failed, as opposed to a request that is wrong.
    /// </summary>
    public bool IsInfrastructureFailure =>
        Code is ErrorCodes.LSP or ErrorCodes.IO or ErrorCodes.TIMEOUT;

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
internal readonly union Result(Success, ToolError)
{
    public static Result Success => RoslynBridge.Success.Instance;
}

/// <summary>
/// The outcome of an operation that returns a value.
/// </summary>
internal readonly union Result<T>(T, ToolError)
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

    public Result<TResult> Bind<TResult>(Func<T, Result<TResult>> next) =>
        this switch
        {
            ToolError error => error,
            T value => next(value),
            null => next(default!),
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
                    return error;

                values.Add(value);
            }

            return values;
        }

        /// <summary>
        /// Skips the errors.
        /// </summary>
        public IEnumerable<T> Values()
        {
            foreach (var result in results)
            {
                if (!result.IsError(out _, out var value))
                    yield return value;
            }
        }
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
    internal const string INVALID_REQUEST = "invalid_request";
    internal const string INVALID_SETTINGS = "invalid_settings";
    internal const string STALE_EDIT = "stale_edit";
    internal const string LOADING = "loading";
    internal const string TIMEOUT = "timeout";
    internal const string CANCELLED = "cancelled";
    internal const string LSP = "lsp_error";
    internal const string IO = "io_error";
    internal const string RESULT_TOO_LARGE = "result_too_large";
    internal const string INTERNAL = "internal_error";
    internal const string SYMBOL_NOT_FOUND = "symbol_not_found";
    internal const string AMBIGUOUS_SYMBOL = "ambiguous_symbol";
    internal const string AMBIGUOUS_FILE = "ambiguous_file";
}
