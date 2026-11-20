// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Common;
using RoslynBridge.Features.Server.Status;
using RoslynBridge.Lsp;
using RoslynBridge.Settings;

namespace RoslynBridge.Features.Server.Reload;

internal sealed class ReloadHandler(
    SettingsMonitor settings,
    WorkspacePaths paths,
    RoslynSession session,
    IToolHandler<StatusRequest, SessionStatus> status
) : IToolHandler<ReloadRequest, SessionStatus>
{
    public async Task<Result<SessionStatus>> HandleAsync(
        ReloadRequest request,
        CancellationToken cancellationToken
    )
    {
        BridgeOptions reloaded;
        try
        {
            reloaded = settings.Reload();
        }
        catch (InvalidSettingsException exception)
        {
            return new ToolError(ErrorCodes.InvalidSettings, exception.Message);
        }

        paths.Exclude(reloaded.Exclude);
        var restarted = await session.ReloadAsync(cancellationToken);
        if (restarted is ToolError error)
            return error;

        return await status.HandleAsync(new StatusRequest(), cancellationToken);
    }
}
