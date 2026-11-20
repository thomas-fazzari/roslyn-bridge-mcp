// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;

namespace RoslynBridge.Features.Server.Status;

internal sealed record StatusRequest : ToolRequest
{
    public override CallPolicy Policy =>
        new(LspAction.Status, ServerPreparation.None, Exclusive: false);
}
