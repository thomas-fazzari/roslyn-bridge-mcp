// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;
using RoslynBridge.Changes;
using RoslynBridge.Editing;
using RoslynBridge.Lsp;
using RoslynBridge.Queries;
using RoslynBridge.Requests;
using RoslynBridge.Results;
using RoslynBridge.Symbols;
using RoslynBridge.Tests.Fakes;
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
        await using var session = CreateSession(_root, FakeServer());
        var changes = CreateChanges(session);
        var snapshot = Snapshot(_root);
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
        await using var session = CreateSession(_root, FakeServer());
        var changes = CreateChanges(session);
        var snapshot = Snapshot(_root);
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
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (_, _) => throw new LocalRpcException("Unknown command.")
            )
        );
        await session.EnsureStartedAsync(TestCt);
        var changes = CreateChanges(session);
        var snapshot = Snapshot(_root);
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
    public async Task SynchronizeFailureAfterAWritePreservesTheCompletedWriteAsync()
    {
        var source = await WriteFileAsync(_root, "Source.cs", "class A {}");
        var destination = Path.Combine(_root, "Renamed.cs");
        var servers = new List<FakeRoslynServer>();
        await using var session = CreateSession(_root, FakeServer(servers.Add));
        await session.EnsureStartedAsync(TestCt);
        var changes = CreateChanges(session);
        var pending = new PendingChange(
            LspAction.RenameFile,
            Snapshot(_root),
            DocumentChanges(RenameFile(source, destination)),
            Command: null
        );

        servers[0].Crash();

        var failure = await FluentActions
            .Awaiting(Apply)
            .Should()
            .ThrowAsync<EditApplicationException>();

        failure.Which.Phase.Should().Be(EditApplicationPhase.Synchronize);
        failure.Which.Result.Applied.Should().BeTrue();
        File.Exists(source).Should().BeFalse();
        await AssertFileTextAsync(destination, "class A {}");
        return;

        Task Apply() => changes.CompleteAsync(pending, apply: true, TestCt);
    }

    [Fact]
    public async Task ListingFlattensNestedActionsAndResolvesFixAllByScopeAsync()
    {
        var path = await WriteFileAsync(_root, "Source.cs", "class A {}");
        JsonNode? fixAll = null;
        await using var session = CreateSession(
            _root,
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

                    return Task.FromResult<JsonNode?>(CodeActions());
                }
            )
        );
        await session.EnsureStartedAsync(TestCt);
        var changes = CreateChanges(session);

        var listing = (ActionListing)
            (
                await changes.ExecuteAsync(
                    new LspRequest
                    {
                        Action = LspAction.CodeActions,
                        File = path,
                        Line = 1,
                        Character = 7,
                    },
                    TestCt
                )
            ).Value!;
        var preview = (ProposalPreview)
            (
                await changes.ExecuteAsync(
                    new LspRequest
                    {
                        Action = LspAction.CodeActions,
                        ProposalId = listing.ProposalId,
                        ActionIndex = 2,
                    },
                    TestCt
                )
            ).Value!;

        listing
            .Actions.Select(action => action.Title)
            .Should()
            .Equal(
                "Wrapping > Wrap call chain",
                "Fix All: Rename (Document)",
                "Fix All: Rename (Project)"
            );
        fixAll!["scope"]!.GetValue<string>().Should().Be("Project");
        preview.Command.Should().BeNull();
        preview.Files.Should().ContainSingle();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static JsonArray CodeActions()
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

    private LspChanges CreateChanges(RoslynSession session)
    {
        var paths = new WorkspacePaths(_root);
        var queries = new LspQueries(session, paths, new SymbolResolver(session, paths));
        return new LspChanges(session, paths, new WorkspaceEditService(paths), queries);
    }

    private static LspRequest Proposal(string proposalId) =>
        new() { Action = LspAction.Rename, ProposalId = proposalId };
}
