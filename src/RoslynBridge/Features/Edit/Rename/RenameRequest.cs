// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;

namespace RoslynBridge.Features.Edit.Rename;

internal sealed record RenameRequest(SymbolTarget Target, string NewName, bool Apply, Paging Page)
    : EditRequest(Apply, Page)
{
    public override LspAction Action => LspAction.Rename;

    public static Result<RenameRequest> From(
        Result<SymbolTarget> target,
        string? newName,
        bool apply,
        Paging page
    ) =>
        string.IsNullOrWhiteSpace(newName)
            ? ToolError.InvalidRequest("Provide newName for the rename.")
            : target.Map(found => new RenameRequest(found, newName, apply, page));

    public override RenameRequest WithFiles(FileResolver resolve) =>
        this with
        {
            Target = Target.WithFile(resolve),
        };
}
