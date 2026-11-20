// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;
using RoslynBridge.Results;

namespace RoslynBridge.Common;

internal interface IToolHandler<in TRequest, TResult>
    where TRequest : ToolRequest
    where TResult : ToolResult
{
    Task<Result<TResult>> HandleAsync(TRequest request, CancellationToken cancellationToken);
}
