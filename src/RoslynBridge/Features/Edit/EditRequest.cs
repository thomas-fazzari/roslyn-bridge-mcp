// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;

namespace RoslynBridge.Features.Edit;

internal closed record EditRequest(bool Apply, Paging Page) : ToolRequest
{
    public override Paging Page { get; } = Page;

    public abstract LspAction Action { get; }

    // A new edit synchronizes Roslyn itself
    protected virtual ServerPreparation Server => ServerPreparation.Start;

    protected virtual FileScope Files => FileScope.CSharp;

    public override CallPolicy Policy => new(Action, Server, MayRestart: Apply, Files: Files);
}
