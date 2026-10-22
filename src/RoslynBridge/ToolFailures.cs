// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel;
using System.Text.Json;
using RoslynBridge.Lsp;
using StreamJsonRpc;

namespace RoslynBridge;

/// <summary>
/// Maps the exceptions that a tool call reports as errors, instead of failing on them, to error codes.
/// </summary>
internal static class ToolFailures
{
    public static bool IsReported(Exception exception) =>
        exception
            is ToolErrorException
                or WorkspaceLoadingException
                or IOException
                or UnauthorizedAccessException
                or Win32Exception
                or RemoteRpcException
                or TimeoutException
                or JsonException;

    public static string FailureCode(Exception exception) =>
        exception switch
        {
            ToolErrorException toolError => toolError.Error.Code,
            WorkspaceLoadingException => ErrorCodes.Loading,
            TimeoutException => ErrorCodes.Timeout,
            IOException or UnauthorizedAccessException or Win32Exception => ErrorCodes.Io,
            RemoteRpcException or JsonException => ErrorCodes.Lsp,
            // Only an application failure reports an unexpected cause, which is a bridge bug
            _ => ErrorCodes.Internal,
        };

    public static ToolError ToError(Exception exception) =>
        new(FailureCode(exception), exception.Message);
}
