// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;

namespace RoslynBridge.Features.Server.Reload;

internal sealed record ReloadRequest : ToolRequest
{
    public override CallPolicy Policy =>
        new(LspAction.Reload, ServerPreparation.None, MayRestart: true);
}
