// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Common;
using RoslynBridge.Lsp;
using RoslynBridge.Settings;

namespace RoslynBridge.Features.Server.Status;

internal sealed class StatusHandler(SettingsMonitor settings, RoslynSession session)
    : IToolHandler<StatusRequest, SessionStatus>
{
    public Task<Result<SessionStatus>> HandleAsync(
        StatusRequest request,
        CancellationToken cancellationToken
    ) =>
        Task.FromResult<Result<SessionStatus>>(
            new SessionStatus(
                session.IsRunning,
                session.IsLoading,
                settings.CurrentValue.WorkspaceRoot,
                settings.CurrentValue.ServerPath,
                session.Progress?.Text
            )
        );
}
