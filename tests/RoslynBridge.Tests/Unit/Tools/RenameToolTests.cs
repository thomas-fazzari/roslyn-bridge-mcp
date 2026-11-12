// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using RoslynBridge.Tests.Assertions;
using RoslynBridge.Tests.Fakes;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class RenameToolTests : TempWorkspaceTests
{
    private const string SourceText = "class A {}\n";

    [Fact]
    public async Task RenamePreviewShowsTheChangedLinesAndApplyingItWritesTheFileAsync()
    {
        var source = await WriteFileAsync(Root, SourceFile, SourceText);
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                    Task.FromResult<JsonNode?>(
                        method is LspMethods.TextDocumentRename
                            ? WorkspaceChanges(source, TextEdit(0, 6, 0, 7, "B"))
                            : null
                    )
            )
        );
        using var tool = CreateTool(Root, session);

        var preview = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Rename,
                File = SourceFile,
                Line = 1,
                Character = 7,
                NewName = "B",
            },
            TestCt
        );

        preview.Should().BeSuccessful();
        var lines = ResultLines.Lines(ResponseText(preview));
        lines[0].Should().EndWith(": 1 file, not applied");
        lines
            .Should()
            .Contain("change Source.cs")
            .And.Contain("- class A {}")
            .And.Contain("+ class B {}");
        await AssertFileTextAsync(source, SourceText);

        var applied = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Rename,
                ProposalId = ProposalId(ResponseText(preview)),
                Apply = true,
            },
            TestCt
        );

        ResponseText(applied).Should().BeApplied().And.Contain("change Source.cs");
        await AssertFileTextAsync(source, "class B {}\n");
    }

    [Fact]
    public async Task RenameFileIsRefusedWithoutTheWillRenameCapabilityAsync()
    {
        await WriteFileAsync(Root, SourceFile, SourceText);
        await using var session = CreateSession(Root, FakeServer());
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(RenameFileRequest(), TestCt);

        response.Should().BeError(ErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task RenameFilePreviewMovesTheFileAsync()
    {
        await WriteFileAsync(Root, SourceFile, SourceText);
        await using var session = CreateSession(
            Root,
            FakeServer(server => server.Capabilities = LspCapabilityJson.WillRenameFiles())
        );
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(RenameFileRequest(), TestCt);

        response.Should().BeSuccessful();
        ResultLines
            .Lines(ResponseText(response))
            .Should()
            .Contain($"delete {SourceFile}")
            .And.Contain($"create {RenamedFile}");
        File.Exists(Path.Combine(Root, RenamedFile)).Should().BeFalse();
    }

    private static LspRequest RenameFileRequest() =>
        new()
        {
            Action = LspAction.RenameFile,
            File = SourceFile,
            NewName = RenamedFile,
        };

    private static string ProposalId(string preview) =>
        preview.Split('\n')[0].Split(' ')[1].TrimEnd(':');
}
