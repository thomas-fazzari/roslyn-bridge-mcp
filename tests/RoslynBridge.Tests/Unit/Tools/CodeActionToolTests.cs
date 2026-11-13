// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using RoslynBridge.Tests.Assertions;
using StreamJsonRpc;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class CodeActionToolTests : TempWorkspaceTests
{
    [Fact]
    public async Task ListingFlattensNestedActionsAndResolvesFixAllByScopeAsync()
    {
        var path = await WriteFileAsync(Root, SourceFile, "class A {}");
        JsonNode? fixAll = null;
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
                server.Handle = (method, parameters) =>
                {
                    if (method is LspMethods.CodeActionResolveFixAll)
                    {
                        fixAll = parameters;
                        return Task.FromResult<JsonNode?>(
                            new JsonObject
                            {
                                ["edit"] = WorkspaceChanges(path, TextEdit(0, 6, 0, 7, "B")),
                            }
                        );
                    }

                    return Task.FromResult<JsonNode?>(
                        method is LspMethods.TextDocumentCodeAction
                            ? NestedAndFixAllActions()
                            : null
                    );
                }
            )
        );
        using var tool = CreateTool(Root, session);

        var listing = ResponseText(await tool.ExecuteAsync(CodeActionsRequest(), TestCt));
        var preview = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.CodeActions,
                ProposalId = ResultLines.ProposalId(listing),
                ActionIndex = 2,
            },
            TestCt
        );

        ResultLines
            .Lines(listing)
            .Skip(1)
            .Should()
            .Equal(
                "0 Wrapping > Wrap call chain",
                "1 Fix All: Rename (Document)",
                "2 Fix All: Rename (Project)"
            );
        fixAll!["scope"]!.GetValue<string>().Should().Be("Project");
        var lines = ResultLines.Lines(ResponseText(preview));
        lines[0].Should().EndWith(": 1 file, not applied");
        lines.Should().NotContain(line => line.StartsWith("command ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedCommandWithoutWritesReportsOnlyTheLspErrorAsync()
    {
        await WriteFileAsync(Root, SourceFile, "class A {}");
        var actions = new JsonArray(
            new JsonObject
            {
                ["title"] = "Run it",
                ["command"] = new JsonObject { ["command"] = "unknown.command" },
            }
        );
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                {
                    if (method is LspMethods.WorkspaceExecuteCommand)
                    {
                        throw new LocalRpcException("Unknown command.");
                    }

                    return Task.FromResult<JsonNode?>(
                        method is LspMethods.TextDocumentCodeAction ? actions.DeepClone() : null
                    );
                }
            )
        );
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(
            CodeActionsRequest() with
            {
                ActionIndex = 0,
                Apply = true,
            },
            TestCt
        );

        response.Should().BeError(ErrorCodes.Lsp);
        ResultLines.Lines(ResponseText(response)).Should().ContainSingle();
    }

    private static LspRequest CodeActionsRequest() =>
        new()
        {
            Action = LspAction.CodeActions,
            File = SourceFile,
            Line = 1,
            Character = 7,
        };

    private static JsonArray NestedAndFixAllActions()
    {
        var wrap = new JsonObject
        {
            ["title"] = "Wrap call chain",
            ["data"] = new JsonObject
            {
                ["CodeActionPath"] = new JsonArray("Wrapping", "Wrap call chain"),
            },
        };
        var fixAllData = new JsonObject
        {
            ["CodeActionPath"] = new JsonArray("Rename"),
            ["FixAllFlavors"] = new JsonArray("Document", "Project"),
        };
        return
        [
            new JsonObject
            {
                ["title"] = "Wrapping",
                ["data"] = new JsonObject { ["NestedCodeActions"] = new JsonArray(wrap) },
            },
            new JsonObject
            {
                ["title"] = "Fix All: Rename",
                ["command"] = new JsonObject
                {
                    ["command"] = "roslyn.client.fixAllCodeAction",
                    ["arguments"] = new JsonArray(fixAllData.DeepClone()),
                },
                ["data"] = fixAllData,
            },
        ];
    }
}
