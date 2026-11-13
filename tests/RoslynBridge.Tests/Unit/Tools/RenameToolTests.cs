// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Changes;
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
        await using var session = CreateSession(Root, RenamingServer());
        using var tool = CreateTool(Root, session);

        var preview = await tool.ExecuteAsync(RenameRequest(), TestCt);

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
            ProposalRequest(ResultLines.ProposalId(ResponseText(preview))) with
            {
                Apply = true,
            },
            TestCt
        );

        ResponseText(applied).Should().BeApplied().And.Contain("change Source.cs");
        await AssertFileTextAsync(source, "class B {}\n");
    }

    [Fact]
    public async Task EvictionKeepsTheNewestProposalsAsync()
    {
        await WriteFileAsync(Root, SourceFile, SourceText);
        await using var session = CreateSession(Root, RenamingServer());
        using var tool = CreateTool(Root, session);
        var proposals = new List<string>();
        for (var index = 0; index < 2 * LspChanges.MaximumPendingProposals; index++)
        {
            var preview = await tool.ExecuteAsync(RenameRequest(), TestCt);
            proposals.Add(ResultLines.ProposalId(ResponseText(preview)));
        }

        var oldest = await tool.ExecuteAsync(ProposalRequest(proposals[0]), TestCt);
        oldest.Should().BeError(ErrorCodes.InvalidRequest);

        // Each use remembers a new preview, which evicts the oldest remaining proposal
        foreach (var proposal in proposals[^LspChanges.MaximumPendingProposals..])
        {
            var response = await tool.ExecuteAsync(ProposalRequest(proposal), TestCt);
            response.Should().BeSuccessful();
        }
    }

    [Fact]
    public async Task ProposalCannotBeUsedForAnotherActionAsync()
    {
        await WriteFileAsync(Root, SourceFile, SourceText);
        await using var session = CreateSession(Root, RenamingServer());
        using var tool = CreateTool(Root, session);
        var preview = await tool.ExecuteAsync(RenameRequest(), TestCt);

        var response = await tool.ExecuteAsync(
            ProposalRequest(ResultLines.ProposalId(ResponseText(preview))) with
            {
                Action = LspAction.RenameFile,
            },
            TestCt
        );

        response.Should().BeError(ErrorCodes.InvalidRequest);
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

    [Fact]
    public async Task RoslynExitAfterTheWriteReportsTheMovedFileAsync()
    {
        var source = await WriteFileAsync(Root, SourceFile, SourceText);
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
            {
                server.Capabilities = LspCapabilityJson.WillRenameFiles();
                // The bridge asks for the project contexts only after it wrote the files
                server.Handle = (method, _) =>
                {
                    if (method is LspMethods.TextDocumentGetProjectContexts)
                    {
                        server.Crash();
                    }

                    return Task.FromResult<JsonNode?>(null);
                };
            })
        );
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(RenameFileRequest() with { Apply = true }, TestCt);

        response.Should().BeError(ErrorCodes.Io);
        ResultLines
            .Lines(ResponseText(response))
            .Should()
            .Contain("failed step: synchronize")
            .And.Contain("applied: 2 files, 2 operations, Roslyn not synchronized")
            .And.Contain($"create {RenamedFile}")
            .And.Contain($"delete {SourceFile}");
        File.Exists(source).Should().BeFalse();
        await AssertFileTextAsync(Path.Combine(Root, RenamedFile), SourceText);
    }

    private RoslynLauncher RenamingServer() =>
        FakeServer(server =>
            server.Handle = (method, _) =>
                Task.FromResult<JsonNode?>(
                    method is LspMethods.TextDocumentRename
                        ? WorkspaceChanges(
                            Path.Combine(Root, SourceFile),
                            TextEdit(0, 6, 0, 7, "B")
                        )
                        : null
                )
        );

    private static LspRequest RenameRequest() =>
        new()
        {
            Action = LspAction.Rename,
            File = SourceFile,
            Line = 1,
            Character = 7,
            NewName = "B",
        };

    private static LspRequest ProposalRequest(string proposalId) =>
        new() { Action = LspAction.Rename, ProposalId = proposalId };

    private static LspRequest RenameFileRequest() =>
        new()
        {
            Action = LspAction.RenameFile,
            File = SourceFile,
            NewName = RenamedFile,
        };
}
