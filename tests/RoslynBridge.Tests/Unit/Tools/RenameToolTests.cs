// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.Time.Testing;
using RoslynBridge.Changes;
using RoslynBridge.Lsp;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Requests;
using RoslynBridge.Tests.Assertions;
using RoslynBridge.Tests.Fakes;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class RenameToolTests : TempWorkspaceTests
{
    private const string SOURCE_TEXT = "class A {}\n";

    [Fact]
    public async Task RenamePreviewShowsTheChangedLinesAndApplyingItWritesTheFileAsync()
    {
        var source = await WriteFileAsync(Root, SOURCE_FILE, SOURCE_TEXT);
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
        await AssertFileTextAsync(source, SOURCE_TEXT);

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
        await WriteFileAsync(Root, SOURCE_FILE, SOURCE_TEXT);
        await using var session = CreateSession(Root, RenamingServer());
        using var tool = CreateTool(Root, session);
        var proposals = new List<string>();
        for (var index = 0; index < 2 * ProposalStore.MAXIMUM_PENDING_PROPOSALS; index++)
        {
            var preview = await tool.ExecuteAsync(RenameRequest(), TestCt);
            proposals.Add(ResultLines.ProposalId(ResponseText(preview)));
        }

        var oldest = await tool.ExecuteAsync(ProposalRequest(proposals[0]), TestCt);
        oldest.Should().BeError(ErrorCodes.INVALID_REQUEST);

        // Each use remembers a new preview, which evicts the oldest remaining proposal
        foreach (var proposal in proposals[^ProposalStore.MAXIMUM_PENDING_PROPOSALS..])
        {
            var response = await tool.ExecuteAsync(ProposalRequest(proposal), TestCt);
            response.Should().BeSuccessful();
        }
    }

    [Fact]
    public async Task ProposalExpiresAfterItsLifetimeAsync()
    {
        var source = await WriteFileAsync(Root, SOURCE_FILE, SOURCE_TEXT);
        await using var session = CreateSession(Root, RenamingServer());
        var time = new FakeTimeProvider();
        using var tool = CreateTool(Root, session, time: time);
        var preview = await tool.ExecuteAsync(RenameRequest(), TestCt);

        time.Advance(ProposalStore.ProposalLifetime + TimeSpan.FromSeconds(1));
        var applied = await tool.ExecuteAsync(
            ProposalRequest(ResultLines.ProposalId(ResponseText(preview))) with
            {
                Apply = true,
            },
            TestCt
        );

        applied.Should().BeError(ErrorCodes.INVALID_REQUEST);
        await AssertFileTextAsync(source, SOURCE_TEXT);
    }

    [Fact]
    public async Task ProposalCannotBeUsedForAnotherActionAsync()
    {
        await WriteFileAsync(Root, SOURCE_FILE, SOURCE_TEXT);
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

        response.Should().BeError(ErrorCodes.INVALID_REQUEST);
    }

    [Theory]
    [InlineData(nameof(LspAction.Rename), 0)]
    [InlineData(nameof(LspAction.Rename), Paging.MAXIMUM_LIMIT + 1)]
    [InlineData(nameof(LspAction.RenameFile), 0)]
    [InlineData(nameof(LspAction.RenameFile), Paging.MAXIMUM_LIMIT + 1)]
    [InlineData(nameof(LspAction.CodeActions), 0)]
    [InlineData(nameof(LspAction.CodeActions), Paging.MAXIMUM_LIMIT + 1)]
    public async Task EditRejectsALimitOutsideTheAllowedRangeAsync(string action, int limit)
    {
        await WriteFileAsync(Root, SOURCE_FILE, SOURCE_TEXT);
        await using var session = CreateSession(Root, RenamingServer());
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(
            RenameRequest() with
            {
                Action = Enum.Parse<LspAction>(action),
                Limit = limit,
            },
            TestCt
        );

        response.Should().BeError(ErrorCodes.INVALID_REQUEST);
    }

    [Fact]
    public async Task RenameFileIsRefusedWithoutTheWillRenameCapabilityAsync()
    {
        await WriteFileAsync(Root, SOURCE_FILE, SOURCE_TEXT);
        await using var session = CreateSession(Root, FakeServer());
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(RenameFileRequest(), TestCt);

        response.Should().BeError(ErrorCodes.INVALID_REQUEST);
    }

    [Fact]
    public async Task RenameFilePreviewMovesTheFileAsync()
    {
        await WriteFileAsync(Root, SOURCE_FILE, SOURCE_TEXT);
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
            .Contain($"delete {SOURCE_FILE}")
            .And.Contain($"create {RENAMED_FILE}");
        File.Exists(Path.Combine(Root, RENAMED_FILE)).Should().BeFalse();
    }

    [Fact]
    public async Task RenameFileCanChangeOnlyTheCaseOfTheNameAsync()
    {
        SkipOnCaseSensitiveFileSystem();
        await WriteFileAsync(Root, SOURCE_FILE, SOURCE_TEXT);
        await using var session = CreateSession(
            Root,
            FakeServer(server => server.Capabilities = LspCapabilityJson.WillRenameFiles())
        );
        using var tool = CreateTool(Root, session);
        var lowerCase = SOURCE_FILE.ToLowerInvariant();

        var response = await tool.ExecuteAsync(
            RenameFileRequest() with
            {
                NewName = lowerCase,
                Apply = true,
            },
            TestCt
        );

        response.Should().BeSuccessful();
        Directory.GetFiles(Root).Select(Path.GetFileName).Should().Equal(lowerCase);
    }

    [Fact]
    public async Task RoslynExitAfterTheWriteReportsTheMovedFileAsync()
    {
        var source = await WriteFileAsync(Root, SOURCE_FILE, SOURCE_TEXT);
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
            {
                server.Capabilities = LspCapabilityJson.WillRenameFiles();
                // The bridge asks for the project contexts only after it wrote the files
                server.Handle = (method, _) =>
                {
                    if (method is LspMethods.TEXT_DOCUMENT_GET_PROJECT_CONTEXTS)
                        server.Crash();

                    return Task.FromResult<JsonNode?>(null);
                };
            })
        );
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(RenameFileRequest() with { Apply = true }, TestCt);

        response.Should().BeError(ErrorCodes.IO);
        ResultLines
            .Lines(ResponseText(response))
            .Should()
            .Contain("failed step: synchronize")
            .And.Contain("applied: 2 files, 2 operations, Roslyn not synchronized")
            .And.Contain($"create {RENAMED_FILE}")
            .And.Contain($"delete {SOURCE_FILE}");
        File.Exists(source).Should().BeFalse();
        await AssertFileTextAsync(Path.Combine(Root, RENAMED_FILE), SOURCE_TEXT);
    }

    [Fact]
    public async Task RoslynCrashDiscardsThePendingProposalsAsync()
    {
        var source = await WriteFileAsync(Root, SOURCE_FILE, SOURCE_TEXT);
        var renaming = RenamingServer();
        var servers = new List<FakeRoslynServer>();
        await using var session = CreateSession(Root, (options, paths) =>
        {
            var server = (FakeRoslynServer)renaming(options, paths);
            servers.Add(server);
            return server;
        });
        using var tool = CreateTool(Root, session);
        var preview = await tool.ExecuteAsync(RenameRequest(), TestCt);

        servers[0].Crash();
        var applied = await tool.ExecuteAsync(
            ProposalRequest(ResultLines.ProposalId(ResponseText(preview))) with
            {
                Apply = true,
            },
            TestCt
        );

        applied.Should().BeError(ErrorCodes.INVALID_REQUEST);
        servers.Should().HaveCount(2);
        await AssertFileTextAsync(source, SOURCE_TEXT);
    }

    private RoslynLauncher RenamingServer() =>
        FakeServer(server =>
            server.Handle = (method, _) =>
                Task.FromResult<JsonNode?>(
                    method is LspMethods.TEXT_DOCUMENT_RENAME
                        ? WorkspaceChanges(
                            Path.Combine(Root, SOURCE_FILE),
                            TextEdit(0, 6, 0, 7, "B")
                        )
                        : null
                )
        );

    private static LspRequest RenameRequest() =>
        new()
        {
            Action = LspAction.Rename,
            File = SOURCE_FILE,
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
            File = SOURCE_FILE,
            NewName = RENAMED_FILE,
        };
}
