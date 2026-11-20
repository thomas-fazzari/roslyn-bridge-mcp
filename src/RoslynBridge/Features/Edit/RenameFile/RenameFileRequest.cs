// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;

namespace RoslynBridge.Features.Edit.RenameFile;

/// <param name="NewName">The workspace-relative destination.</param>
internal sealed record RenameFileRequest(string File, string NewName, bool Apply, Paging Page)
    : EditRequest(Apply, Page)
{
    public override LspAction Action => LspAction.RenameFile;

    protected override FileScope Files => FileScope.AnyFile;

    public static Result<RenameFileRequest> From(
        string? file,
        string? newName,
        bool apply,
        Paging page
    ) =>
        string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(newName)
            ? ToolError.InvalidRequest("Provide file and newName for rename_file.")
            : new RenameFileRequest(file, newName, apply, page);

    public override RenameFileRequest WithFiles(FileResolver resolve) =>
        this with
        {
            File = resolve(File),
        };
}
