// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Tests.Assertions;
using StreamJsonRpc;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class EditRecoveryTests : TempWorkspaceTests
{
    private const string UNTOUCHED_FILE = "Untouched.cs";
    private const string SOURCE_TEXT = "class A {}";

    [Fact]
    public async Task FailedCodeActionCommandAfterAWriteDropsProposalsAndReloadsRoslynAsync()
    {
        var failing = new LspRequest
        {
            Action = LspAction.CodeActions,
            File = SOURCE_FILE,
            Line = 1,
            Character = 7,
            ActionIndex = 0,
            Apply = true,
        };

        await AssertRecoversAsync(failing, serverWritesInCommand: false);
    }

    [Fact]
    public async Task FailedRawCommandAfterAWriteDropsProposalsAndReloadsRoslynAsync()
    {
        var failing = new LspRequest
        {
            Action = LspAction.Request,
            Method = LspMethods.WORKSPACE_EXECUTE_COMMAND,
            Parameters = new JsonObject { ["command"] = "test.command" },
            Apply = true,
        };

        await AssertRecoversAsync(failing, serverWritesInCommand: true);
    }

    private async Task AssertRecoversAsync(LspRequest failing, bool serverWritesInCommand)
    {
        var source = await WriteFileAsync(Root, SOURCE_FILE, SOURCE_TEXT);
        var untouched = await WriteFileAsync(Root, UNTOUCHED_FILE, SOURCE_TEXT);
        var launches = 0;
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
            {
                launches++;
                server.Handle = async (method, _) =>
                {
                    if (method is LspMethods.TEXT_DOCUMENT_RENAME)
                        return WorkspaceChanges(untouched, TextEdit(0, 6, 0, 7, "B"));

                    if (method is LspMethods.TEXT_DOCUMENT_CODE_ACTION)
                        return CodeActionWithEditAndCommand(source);

                    if (method is not LspMethods.WORKSPACE_EXECUTE_COMMAND)
                        return null;

                    if (serverWritesInCommand)
                    {
                        await server.RequestClientAsync(
                            LspMethods.WORKSPACE_APPLY_EDIT,
                            new JsonObject
                            {
                                ["edit"] = WorkspaceChanges(source, TextEdit(0, 6, 0, 7, "B")),
                            },
                            TestCt
                        );
                    }

                    throw new LocalRpcException("The command failed after a write.");
                };
            })
        );
        using var tool = CreateTool(Root, session);
        var preview = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Rename,
                File = UNTOUCHED_FILE,
                Line = 1,
                Character = 7,
                NewName = "B",
            },
            TestCt
        );
        var proposalId = ResultLines.ProposalId(ResponseText(preview));

        var response = await tool.ExecuteAsync(failing, TestCt);

        response.Should().BeError(ErrorCodes.LSP);
        ResultLines.Lines(ResponseText(response)).Should().Contain("failed step: command");
        launches.Should().Be(1);

        var reused = await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Rename, ProposalId = proposalId },
            TestCt
        );

        reused.Should().BeError(ErrorCodes.INVALID_REQUEST);
        launches.Should().Be(2);
    }

    private static JsonArray CodeActionWithEditAndCommand(string path) =>
        [
            new JsonObject
            {
                ["title"] = "Edit and run",
                ["edit"] = WorkspaceChanges(path, TextEdit(0, 6, 0, 7, "B")),
                ["command"] = new JsonObject { ["command"] = "test.command" },
            },
        ];
}
