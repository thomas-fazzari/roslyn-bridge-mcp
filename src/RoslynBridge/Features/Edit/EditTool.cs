// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Protocol;
using RoslynBridge.Common;
using RoslynBridge.Features.Edit.CodeActions;
using RoslynBridge.Features.Edit.Proposals;
using RoslynBridge.Features.Edit.Rename;
using RoslynBridge.Features.Edit.RenameFile;
using RoslynBridge.Requests;
using RoslynBridge.Results;

namespace RoslynBridge.Features.Edit;

internal sealed class EditTool(
    ToolPipeline pipeline,
    IToolHandler<RenameRequest, ToolResult> rename,
    IToolHandler<RenameFileRequest, ToolResult> renameFile,
    IToolHandler<CodeActionsRequest, ToolResult> codeActions,
    IToolHandler<ProposalRequest, ToolResult> proposal
) : IRoslynTool
{
    public static string Name => "edit";

    public static string Description =>
        "Preview and apply C# renames, file renames, fixes and refactorings. Calls without apply=true return a preview and a proposalId. Apply that proposal with the same action, its proposalId and apply=true. Positions are one-based.";

    public static bool ReadOnly => false;

    public Task<CallToolResult> EditAsync(
        [Description("Edit to preview or apply.")] EditAction action,
        [Description(SymbolTarget.SymbolDescription)] string? symbol = null,
        [Description(SymbolTarget.FileDescription)] string? file = null,
        [Description(SymbolTarget.LineDescription)] int? line = null,
        [Description(SymbolTarget.CharacterDescription)] int? character = null,
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
        [Description(Paging.LimitDescription)]
        [Range(1, Paging.MaximumLimit)]
            int limit = Paging.DefaultLimit,
        CancellationToken cancellationToken = default
    )
    {
        var page = new Paging(limit, 0);
        if (proposalId is not null)
        {
            return pipeline.RunAsync(
                proposal,
                new ProposalRequest(action.ToLspAction(), proposalId, actionIndex, apply, page),
                cancellationToken
            );
        }

        return action switch
        {
            EditAction.Rename => pipeline.RunAsync(
                rename,
                RenameRequest.From(
                    SymbolTarget.From(symbol, file, line, character),
                    newName,
                    apply,
                    page
                ),
                cancellationToken
            ),
            EditAction.RenameFile => pipeline.RunAsync(
                renameFile,
                RenameFileRequest.From(file, newName, apply, page),
                cancellationToken
            ),
            EditAction.CodeActions => pipeline.RunAsync(
                codeActions,
                CodeActionsRequest.From(
                    CodeActionScope.From(
                        diagnostic,
                        symbol,
                        file,
                        line,
                        character,
                        endLine,
                        endCharacter
                    ),
                    actionIndex,
                    all,
                    apply,
                    page
                ),
                cancellationToken
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
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

internal static class EditActions
{
    extension(EditAction action)
    {
        public LspAction ToLspAction() =>
            action switch
            {
                EditAction.Rename => LspAction.Rename,
                EditAction.RenameFile => LspAction.RenameFile,
                EditAction.CodeActions => LspAction.CodeActions,
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            };
    }
}
