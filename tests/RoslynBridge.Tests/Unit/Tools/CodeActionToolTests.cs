// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Tests.Assertions;
using StreamJsonRpc;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class CodeActionToolTests : TempWorkspaceTests
{
    private const string BOTH_FIELDS = "both";
    private const string NULL_OPERATION = "null-operation";
    private const string RENAME_WITHOUT_NEW_URI = "rename-without-new-uri";
    private const string NULL_TEXT_EDIT = "null-text-edit";

    [Fact]
    public async Task ListingFlattensNestedActionsAndResolvesFixAllByScopeAsync()
    {
        var path = await WriteFileAsync(Root, SOURCE_FILE, "class A {}");
        JsonNode? fixAll = null;
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
                server.Handle = (method, parameters) =>
                {
                    if (method is LspMethods.CODE_ACTION_RESOLVE_FIX_ALL)
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
                        method is LspMethods.TEXT_DOCUMENT_CODE_ACTION
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
        await WriteFileAsync(Root, SOURCE_FILE, "class A {}");
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
                    return method is LspMethods.WORKSPACE_EXECUTE_COMMAND
                        ? throw new LocalRpcException("Unknown command.")
                        : Task.FromResult<JsonNode?>(
                            method is LspMethods.TEXT_DOCUMENT_CODE_ACTION
                                ? actions.DeepClone()
                                : null
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

        response.Should().BeError(ErrorCodes.LSP);
        ResultLines.Lines(ResponseText(response)).Should().ContainSingle();
    }

    [Fact]
    public async Task ActionWithACommandSynchronizesTheFileItsEditCreatesAsync()
    {
        await WriteFileAsync(Root, SOURCE_FILE, "class A {}");
        var actions = new JsonArray(
            new JsonObject
            {
                ["title"] = "Extract",
                ["edit"] = DocumentChanges(CreateFile(Path.Combine(Root, "Created.cs"))),
                ["command"] = new JsonObject { ["command"] = "extract.command" },
            }
        );
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                {
                    // A crash while synchronizing fails the call, while the diagnostics after it ignore one
                    if (method is LspMethods.TEXT_DOCUMENT_GET_PROJECT_CONTEXTS)
                        server.Crash();

                    return Task.FromResult<JsonNode?>(
                        method is LspMethods.TEXT_DOCUMENT_CODE_ACTION ? actions.DeepClone() : null
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

        response.Should().BeError(ErrorCodes.IO);
        ResultLines.Lines(ResponseText(response)).Should().Contain("failed step: synchronize");
    }

    [Theory]
    [InlineData(BOTH_FIELDS)]
    [InlineData(NULL_OPERATION)]
    [InlineData(RENAME_WITHOUT_NEW_URI)]
    [InlineData(NULL_TEXT_EDIT)]
    public async Task ActionWithAMalformedEditWritesNothingAsync(string fault)
    {
        var path = await WriteFileAsync(Root, SOURCE_FILE, "class A {}");
        var actions = new JsonArray(
            new JsonObject { ["title"] = "Broken", ["edit"] = MalformedEdit(fault, path) }
        );
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                    Task.FromResult<JsonNode?>(
                        method is LspMethods.TEXT_DOCUMENT_CODE_ACTION ? actions.DeepClone() : null
                    )
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

        response.Should().BeError(ErrorCodes.LSP);
        await AssertFileTextAsync(path, "class A {}");
    }

    // Each edit starts with a valid change, so a write before the fault would show
    private static JsonObject MalformedEdit(string fault, string path)
    {
        var valid = TextEdit(0, 6, 0, 7, "B");
        var uri = new Uri(path).AbsoluteUri;
        return fault switch
        {
            BOTH_FIELDS => new JsonObject
            {
                ["changes"] = new JsonObject { [uri] = new JsonArray(valid) },
                ["documentChanges"] = new JsonArray(),
            },
            NULL_OPERATION => new JsonObject
            {
                ["documentChanges"] = new JsonArray(DocumentEdit(path, version: null, valid), null),
            },
            RENAME_WITHOUT_NEW_URI => DocumentChanges(
                DocumentEdit(path, version: null, valid),
                new JsonObject { ["kind"] = "rename", ["oldUri"] = uri }
            ),
            NULL_TEXT_EDIT => new JsonObject
            {
                ["changes"] = new JsonObject { [uri] = new JsonArray(valid, null) },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };
    }

    private static LspRequest CodeActionsRequest() =>
        new()
        {
            Action = LspAction.CodeActions,
            File = SOURCE_FILE,
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
