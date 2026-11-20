// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;

namespace RoslynBridge.Features.Server.Capabilities;

internal sealed record CapabilitiesRequest : ToolRequest
{
    public override CallPolicy Policy => new(LspAction.Capabilities);
}
