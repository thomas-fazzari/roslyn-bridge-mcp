// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynBridge.Changes;
using RoslynBridge.Editing;
using RoslynBridge.Lsp;
using RoslynBridge.Queries;
using RoslynBridge.Requests;
using RoslynBridge.Symbols;
using RoslynBridge.Tests.Fakes;
using RoslynBridge.Tools;
using StreamJsonRpc;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Changes;

public sealed class LspChangesTests : IDisposable
{
    private readonly string _root = Directory
        .CreateTempSubdirectory("roslyn-lsp-changes-")
        .FullName;

    [Fact]
    public async Task EvictionKeepsTheNewestProposalsAsync()
    {
        await using var session = CreateSession();
        var (changes, edits) = CreateChanges(session);
        var snapshot = await edits.CaptureAsync(TestCt);
        var proposals = new List<string>();
        for (var index = 0; index < 2 * LspChanges.MaximumPendingProposals; index++)
        {
            var preview = await changes.CompleteAsync(
                new PendingChange(LspAction.Rename, snapshot, Edit: [], Command: null),
                apply: false,
                TestCt
            );
            proposals.Add(((ProposalPreview)preview).ProposalId);
        }

        var oldest = await changes.ExecuteAsync(Proposal(proposals[0]), TestCt);
        oldest.Error!.Code.Should().Be(ErrorCodes.InvalidRequest);

        // Each use remembers a new preview, which evicts the oldest remaining proposal
        foreach (var proposal in proposals[^LspChanges.MaximumPendingProposals..])
        {
            var result = await changes.ExecuteAsync(Proposal(proposal), TestCt);
            result.Value.Should().BeOfType<ProposalPreview>();
        }
    }

    [Fact]
    public async Task ProposalCannotBeUsedForAnotherActionAsync()
    {
        await using var session = CreateSession();
        var (changes, edits) = CreateChanges(session);
        var snapshot = await edits.CaptureAsync(TestCt);
        var preview = await changes.CompleteAsync(
            new PendingChange(LspAction.Rename, snapshot, Edit: [], Command: null),
            apply: false,
            TestCt
        );

        var result = await changes.ExecuteAsync(
            Proposal(((ProposalPreview)preview).ProposalId) with
            {
                Action = LspAction.RenameFile,
            },
            TestCt
        );

        result.Error!.Code.Should().Be(ErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task FailedCommandWithoutWritesKeepsTheOriginalErrorAsync()
    {
        await using var session = CreateSession(server =>
            server.Handle = (_, _) => throw new LocalRpcException("Unknown command.")
        );
        await session.EnsureStartedAsync(TestCt);
        var (changes, edits) = CreateChanges(session);
        var snapshot = await edits.CaptureAsync(TestCt);
        var command = new JsonObject { ["command"] = "unknown.command" };

        await FluentActions.Awaiting(Apply).Should().ThrowAsync<RemoteRpcException>();
        return;

        Task Apply() =>
            changes.CompleteAsync(
                new PendingChange(LspAction.CodeActions, snapshot, Edit: null, command),
                apply: true,
                TestCt
            );
    }

    [Fact]
    public async Task ReloadFailureAfterRenamePreservesTheCompletedWriteAsync()
    {
        var path = await WriteFileAsync(_root, "Source.cs", "class A {}");
        // Reload after the rename fails fast on a missing server
        await using var session = new RoslynSession(
            new BridgeOptions
            {
                WorkspaceRoot = _root,
                ServerPath = Path.Combine(_root, "missing-server"),
            },
            new WorkspacePaths(_root),
            RoslynProcess.Start,
            NullLogger<RoslynSession>.Instance
        );
        var (changes, edits) = CreateChanges(session);
        var snapshot = await edits.CaptureAsync(TestCt);
        var pending = new PendingChange(
            LspAction.RenameFile,
            snapshot,
            WorkspaceChanges(path, TextEdit(0, 6, 0, 7, "B")),
            Command: null,
            RenamesFiles: true
        );

        var failure = await FluentActions
            .Awaiting(Apply)
            .Should()
            .ThrowAsync<EditApplicationException>();

        failure.Which.Phase.Should().Be(EditApplicationPhase.Synchronize);
        ErrorCode(LspTool.ApplicationFailure(failure.Which, cancellationRequested: false))
            .Should()
            .Be(ErrorCodes.Io);
        failure.Which.Result.Applied.Should().BeTrue();
        failure.Which.Result.FileCount.Should().Be(1);
        await AssertFileTextAsync(path, "class B {}");
        return;

        Task Apply() => changes.CompleteAsync(pending, apply: true, TestCt);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private RoslynSession CreateSession(Action<FakeRoslynServer>? configure = null) =>
        new(
            new BridgeOptions { WorkspaceRoot = _root },
            new WorkspacePaths(_root),
            (_, _) =>
            {
                var server = new FakeRoslynServer();
                configure?.Invoke(server);
                return server;
            },
            NullLogger<RoslynSession>.Instance
        );

    private (LspChanges Changes, WorkspaceEditService Edits) CreateChanges(RoslynSession session)
    {
        var paths = new WorkspacePaths(_root);
        var edits = new WorkspaceEditService(paths);
        var queries = new LspQueries(session, paths, new SymbolResolver(session, paths));
        return (new LspChanges(session, paths, edits, queries), edits);
    }

    private static LspRequest Proposal(string proposalId) =>
        new() { Action = LspAction.Rename, ProposalId = proposalId };
}
