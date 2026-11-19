// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RoslynBridge.Requests;

namespace RoslynBridge.Tools;

[McpServerToolType]
internal sealed class EditTool(LspTool lsp)
{
    internal const string Name = "edit";

    [McpServerTool(
        Name = Name,
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false
    )]
    [Description(
        "Preview and apply C# renames, file renames, fixes and refactorings. Calls without apply=true return a preview and a proposalId. Apply that proposal with the same action, its proposalId and apply=true. Positions are one-based."
    )]
    public Task<CallToolResult> EditAsync(
        [Description("Edit to preview or apply.")] EditAction action,
        [Description(ToolDescriptions.Symbol)] string? symbol = null,
        [Description(ToolDescriptions.File)] string? file = null,
        [Description(ToolDescriptions.Line)] int? line = null,
        [Description(ToolDescriptions.Character)] int? character = null,
        [Description("Optional one-based end line for a code action selection.")]
            int? endLine = null,
        [Description("Optional one-based UTF-16 end character for a code action selection.")]
            int? endCharacter = null,
        [Description("New symbol name, or the workspace-relative destination for rename_file.")]
            string? newName = null,
        [Description(
            "Diagnostic code, such as CS0246, whose fixes code_actions lists. Targets its first occurrence in file, or on line. Replaces the position."
        )]
            string? diagnostic = null,
        [Description("Zero-based index of a code action from a fresh listing.")]
            int? actionIndex = null,
        [Description("Proposal identifier returned by a previous preview or listing.")]
            string? proposalId = null,
        [Description("Apply the proposal. Without it, the call only returns a preview.")]
            bool apply = false,
        [Description("Also list suppress or configure code actions.")] bool all = false,
        [Description(ToolDescriptions.Limit)] int limit = Paging.DefaultLimit,
        CancellationToken cancellationToken = default
    ) =>
        lsp.ExecuteAsync(
            action switch
            {
                EditAction.Rename => new RenameRequest(
                    new SymbolTarget(symbol, file, line, character),
                    newName,
                    proposalId,
                    apply,
                    new Paging(limit, 0)
                ),
                EditAction.RenameFile => new RenameFileRequest(
                    file,
                    newName,
                    proposalId,
                    apply,
                    new Paging(limit, 0)
                ),
                EditAction.CodeActions => new CodeActionsRequest(
                    new SymbolTarget(symbol, file, line, character),
                    endLine,
                    endCharacter,
                    diagnostic,
                    actionIndex,
                    all,
                    new Paging(limit, 0),
                    proposalId,
                    apply
                ),
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            },
            cancellationToken
        );
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum EditAction
{
    [JsonStringEnumMemberName("rename")]
    Rename = 0,

    [JsonStringEnumMemberName("rename_file")]
    RenameFile = 1,

    [JsonStringEnumMemberName("code_actions")]
    CodeActions = 2,
}
