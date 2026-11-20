// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Common;
using RoslynBridge.Lsp;

namespace RoslynBridge.Features.Server.Capabilities;

internal sealed class CapabilitiesHandler(RoslynSession session)
    : IToolHandler<CapabilitiesRequest, JsonResult>
{
    public Task<Result<JsonResult>> HandleAsync(
        CapabilitiesRequest request,
        CancellationToken cancellationToken
    ) => Task.FromResult<Result<JsonResult>>(new JsonResult(session.Capabilities?.DeepClone()));
}
